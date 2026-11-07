# Razonamiento de los interruptores SHARPEMU_* (complemento del registro)

Fecha: 2026-11-07. Acompaña a `sharpemu-flags.md` (misma agrupación por áreas y mismos nombres sin el prefijo `SHARPEMU_`). El registro dice **qué** se midió; este fichero dice **por qué** se creó cada interruptor, qué hipótesis probaba y por qué quedó encendido o apagado, según las conversaciones de Claude Code (sesión principal de SharpEmu `3d7ca311`, 2026-10-06 → 2026-11-07, y sus subagentes), la bitácora y los commits.

- 472 interruptores; **451 con razonamiento**, 21 sin nada sólido en las transcripciones (se deja vacío, no se inventa).
- Fuente: `3d7ca311 AAAA-MM-DD` = sesión y día del fragmento; `commit hash` = cuerpo del commit. Las transcripciones anteriores al 2026-10-06 ya no existen: lo de septiembre solo consta en la bitácora y los commits.
- Etiquetas de patrón: `otro` 93, `ganancia` 76, `exacto-neutro` 65, `corrección` 55, `A/B-upstream` 38, `adelanta-cuello` 35, `sin-A/B` 27, `estabilidad` 26, `coste-sonda` 22, `modo-lento` 14.

## Lecciones transversales

Por qué se encienden, se apagan o se descartan los interruptores, según las conversaciones. Sirve para no reproponer lo mismo con otro nombre.

1. **«Exacto pero neutro» (65 casos): cuando el fotograma está limitado por latencia, ahorrar CPU no da FPS.** Muchas optimizaciones pasaban el verify con 0 discrepancias y aun así no movían el paneo. El tiempo ahorrado se convertía en espera, porque el render ya esperaba a la GPU o al front. Ejemplos: `WRITTEN_OBTAIN_MEMO`, `READ_OBTAIN_MEMO`, `SYNC_READBACK_QUEUE`, `UNIFORM_IMAGE_SOURCE`, `CUT_POLICY`, `MATERIALIZE_SYNC_VALIDATE`. Desde el 2026-10-15 la regla es integrarlas apagadas y volver a probarlas cuando el bucle del juego sea más rápido; según un resumen de compactación, Daniel estuvo de acuerdo. El criterio vigente es aceptar por ms de mecanismo y no por FPS (memoria `sharpemu-criterio-ms`). Así entraron encendidos `PRECISE_WRITE_TICKS`, `IDLE_FLUSH`, `FRONT_PREP_RANGES` y `VALIDATE_IN_PLACE`, aunque sus FPS estaban dentro del ruido.
2. **Quitar una espera adelanta el siguiente cuello (35 casos).** Es el motivo de descarte más repetido en la cadena de lecturas síncronas (TLAS/BVH, tablas 994A/B198/F43B/5981). Cada lectura quitada destapaba la siguiente: `DEVICE_DATA_READS`, `CONSERVATIVE_RECORDS` (la espera de 5981 se pasaba a B198), `DEVICE_TABLE_HASHES`, `GATE_PREFETCH` (puerta de 9,5 a 0,2 ms, pero el render pierde 0,6 FPS), `PRESENT_PROMPT`, `UPLOAD_GPU_WAIT` y `EARLY_WRITER_SUBMIT` (convierten una espera en otra). Una variante sutil: una lectura síncrona puede servir de corte de lote temprano. Al quitar la de 994A (`TABLE_WORD_MEMO_LEARN`), el refit de 5981 llegaba tarde a la GPU y el paneo perdía 0,5 FPS, hasta que `PRECISE_WRITE_TICKS` lo compensó. `TABLE_WORD_MEMO_LEARN_FLUSH` intentó conservar ese corte sin esperar y tampoco ganó. Antes de proponer quitar una espera, mira qué espera viene detrás (sondas `READBACK_ORIGIN_PROBE`, `SRT_READ_PROBE`, `TABLE_WORD_MEMO_MISS_PROBE`).
3. **El modo lento (14 casos) obligó a crear sondas y a apagar optimizaciones.** Son sesiones bimodales: la GPU al 100 % a 0,3–3 FPS, o un front parado en REWIND. Para cazarlo nacieron sondas y trampas: `BLAS_CYCLE` y sus subparámetros, `SLOW_MODE_PROBE` (nunca se armó en 20 sesiones), `LABEL_PROBE`/`LABEL_TIMELINE`, `GUEST_CALL_TIMELINE` y `STORE_TABLE_RESCAN`. Su causa real, un despertar perdido del front en `sceAgcRewindPatchSetRewindState`, llevó a `REWIND_PATCH_WAKE=3` por defecto. Un BLAS con ciclos por el solapado de escrituras del front apagó ese solapado (`SPIN_BLOB_DUMP`, `STORE_TABLE_LOG`). Una sola sesión en modo lento contamina un A/B: `VALIDATE_IN_PLACE` y `FRONT_PREP_RANGES` lo muestran, porque su diferencia de FPS la explica casi entera una sesión atípica. Exclúyela y repón la sesión antes de comparar.
4. **Las sondas cuestan, y las medidas de la bitácora llevan ese coste (22 casos).** `PROFILE_RENDER` cuesta unos 5–8 ms al render, y con `PROFILE_GPU_DRAWS` las sesiones rondan los 22 FPS frente a 25,5 sin ellas. `READBACK_WAIT_MS` contaminó un A/B de `READBACK_PRECISE_WAIT` (control a 28,28 FPS frente a ~23,8). El muestreo de pilas de `VKCALL_PROBE` costaba ~16 ms/fotograma, así que pasó a opt-in. Las sondas BVH olvidadas encendidas escribían 330 000 líneas de traza por ejecución (`BVH_PROBES`). Por eso `RENDER_CPU_PROBE` mide por ciclos sin instrumentar. Las cifras absolutas de FPS de la bitácora no se comparan entre sesiones con y sin perfil.
5. **Defecto «lo nuestro», upstream opt-in (38 casos A/B upstream).** Tras la fusión de `origin/main` (2026-11-03/04) GTA V cayó a ~4,7–4,8 FPS. Se fueron cambiando a lo nuestro los defectos de upstream sospechosos: `ACQUIRE_BARRIER`, `MAPPED_BUFFERS`, `BDA_FAULT_WINDOW_BYTES`, `BDA_SWEEP_PLAN`, `FAULT_WRITE_GRANULE`, `PENDING_METADATA_FILLS`, `IMAGE_PUBLICATION`… Varios resultaron no ser la causa (fz24, fz27), pero se quedaron así por la decisión de Daniel, relatada en los encargos a subagentes: donde upstream cambió el comportamiento y lo nuestro funciona en GTA, interruptor con defecto lo nuestro y upstream opcional. Las ideas de upstream adaptadas que no ganan en GTA quedan apagadas: `READBACK_SLOTS`/`READBACK_SLOT_WAIT` (los slots nunca se llenan en GTA), `NATIVE_STATE_REUSE` (ruido) y `HEAT_DECAY`.
6. **Estabilidad antes que FPS (26 casos).** El TLAS en el host (`TLAS_HLE` y su familia `_BELONGS`, `_LEARN`, `_RECENT`) era exacto según el verify y quitaba lecturas síncronas, pero acabó en DeviceLost en 5–6 de cada ~14 sesiones. La causa fue un anillo de TLAS por fotograma que el juego reutiliza, y se descartó entero. `STRICT_COMPUTE=0` provocó TDR. `READBACK_SUBMIT_GUARD` y `STABLE_TABLE_READS_WRITER_MAX` nacieron de cuelgues de la intro, de 1 cada 50–140 sesiones. Con cuelgues raros, 0 de 3 o 0 de 8 sesiones limpias no prueba nada: con p=0,25, tres sesiones limpias salen un 42 % de las veces por azar (`BVHL_PROBE`, `FRONT_PREP_CHECK`, `PRERESERVE_MIN`).
7. **Se encendieron sin A/B aislado (27 casos).** Muchos ON entraron dentro de un paquete (el lote de #1082, el paquete de recortes del 12–15-10, el brazo H, los seis defectos de gz05/gz10) o por ser exactos por construcción: `READBACK_PIECES`, `TOUCH_ONCE_PER_TICK`, `DCC_CLEAR_LEAN`, `LEAN_TEXTURE_PATH`, `CPU_DIRTY_SKIP`, `SPAN_SNAPSHOT`. Si uno resulta sospechoso, su contribución individual está sin medir. No los cuentes como «medidos, ganan».
8. **El primer A/B de un paquete puede salir negativo por una causa ajena.** El brazo M del 15-10 (`STRIPED_ACCESS_COUNT`, `HLE_SPAN_WRITES`, `SINGLE_VIEW_WRITES`) dio peor estática porque el juego enviaba antes y se duplicaban las esperas de GPU. Arregladas esas esperas, el mismo paquete dio +3–4 FPS el 17-10. Antes de descartar, pregunta qué espera se ha movido.

## Contradicciones entre transcripciones y registro (46)

Manda el código; esto señala dónde el registro o la memoria pueden estar desfasados.

- `BUFFER_INT64_ATOMICS` (ON): El registro dice que el interruptor solo existe para A/B sin cifras; sí hubo A/B en el juego (2,94 vs 6,60 FPS).
- `DEFERRED_PROTECT_WORKERS` (ON): El registro da +0,6-0,8 FPS; el informe del 31-10 indica que en el segundo A/B la ganancia baja a +0,1 si se excluye una sesión base de 26,55.
- `DEFERRED_UPLOAD_WAIT_BLOCK` (ON): (resuelve la incoherencia del registro) 80b6abbf lo creó apagado, pero el mismo 16-10 pasó a encendido (el código lee != «0» según el parche de ese día).
- `FAST_FILL` (ON): (resuelve la incoherencia del registro) f7ab3b87 lo dejó opcional sin ganancia (trozos de 4 KB) y el mismo día 2fc1fe33 lo encendió con trozos de 256 KB.
- `MAPPED_BUFFERS` (OFF): El registro dice «sin A/B propio»; hubo A/B conjuntos (fz24 con otros defaults; gz05/gz10 de los seis defaults) pero ninguno aislado.
- `HEAT_DECAY` (Descartado): El registro marca Def: on, pero el cambio solo vive en la rama gta/heatdecay (no integrado) y está descartado.
- `READBACK_PRECISE_WAIT` (Descartado): Hipótesis: el registro da estática 23,69 vs 23,85, pero el 23-10 el coordinador midió un control de 28,28 FPS y atribuyó los ~23,8 a la rama precisewait2 o a la instrumentación READBACK_WAIT_MS=1, así que esa comparación parece contaminada por la sonda.
- `VALIDATE_IN_PLACE` (ON): El 25,66→27,13 de estática del registro incluye un 21,72 de modo lento en el brazo apagado; se aceptó por mecanismo, no por FPS.
- `STORE_TABLE_CHUNKS` (OFF): El registro dice «sin medir»; sí se midió, pero solo combinado con FAST_IMPORT_GATEWAY y los interruptores de memoria (brazo H, peor que la base en estática).
- `BUFFER_SOURCE_MEMO` (Descartado): El commit 3a5ff60d lo dejó a 1 por defecto; el 06-11 se vio que empeora y se dejó apagado (el comentario del código sigue diciendo «1 by default»).
- `IMAGE_LOOKUP_SLOTS` (Descartado): Registro dice que los fallos «no son colisiones»; las transcripciones (11-10) encontraron que el 91 % de los fallos eran colisiones de ranura por el hash parcial, y el 22-10 con 256 ranuras se veían 141k colisiones frente a 25k con 4096.
- `PROGRAM_GATE` (ON): Los tests 4220+939 en verde citados al integrar eran del build anterior a activarlo por defecto (el propio agente lo corrigió); no se pasaron sobre e1c6bc16 en ese momento.
- `SCRATCH_SWIZZLE` (ON): Con el interruptor apagado el módulo ya no es idéntico byte a byte a la base tras c857c888 (cambia el SPIR-V de todo shader con scratch).
- `COHERENT_BUFFERS` (Descartado): El registro dice que no arregla; sin embargo el 9-oct (15:40) se anota que COHERENT_BUFFERS=1 sí arreglaba el banco del ticket (solo rt164int, con árboles grandes, no mejoró).
- `WAVE64_MULTIWAVE` (Descartado): El registro cita 764260b4 (13-oct) como retirada, pero el 17-oct (bcc53df7) el interruptor seguía vivo (off) y la fusión del 3-nov dice que lo elimina (abe22246).
- `TLAS_HLE` (Descartado): Registro y commit dicen 5 DeviceLost en ~13 sesiones; un resumen de compactación del 23-10 dice 6 de ~14.
- `KEY_PROBE` (Sonda): El registro cita rt188/rt189 como «cotas rancias, causa 3» (bitácora L716), pero la bitácora L718 la descarta: las cotas son 0 de verdad.
- `TLAS_HLE_POISON` (Sonda): El registro dice «sin medir», pero el 22-10 se midió: diferencia de imagen 0,5-2,1 % frente a un ruido de 1,5-2,4 %, concluyendo que nadie los lee.
- `ATTACH_REPORT_TICK` (ON): Tras la fusión del 2026-11-03 el interruptor se sigue leyendo pero es inerte (ReportAttachRepeat sin llamar); el registro lo da como activo sin matiz.
- `DISPATCH_BARRIERS` (ON): El registro dice sin ganancia; la tanda sondas1 del 2026-10-16 anota DISPATCH_BARRIERS=0 como peor.
- `FRONT_PREP_CHECK` (ON): Las 8 sesiones de control con PREP_CHECK=0 y las carreras corregidas no colgaron, así que el efecto del check sobre los cuelgues no está aislado; el registro lo presenta como la solución.
- `FRONT_PREP_RANGES` (ON): El registro presenta paneo 25,98 vs 25,13 como resultado; el agente dice que lo explica casi entero una sola sesión y que se acepta sin ganancia de FPS.
- `FRONT_WRITE_OVERLAY` (ON): El registro ya señala la incoherencia con la memoria/bitácora (apagada, opcional =1): las transcripciones muestran que esa fue la mitigación del 2026-10-10 mañana y que el modo 3 por defecto (09b5e0e0) la superó el mismo día.
- `GUEST_POLL_BACKOFF` (ON): El commit 80b6abbf dice todo apagado y el registro fecha la integración el 2026-10-16; las transcripciones sitúan el cambio de defectos en ef5fb944 el 2026-10-17.
- `HOST_HELPER_LANES` (ON): El registro atribuye los -50 core-ms al interruptor; las transcripciones dicen que el interruptor no ahorra core-ms y que S (bloquear en lugar de girar, backoff) aporta ~-41.
- `RECORD_FILLS` (ON): El 2026-10-10 un comando lo deja opt-in (=="1"); el 2026-11-03 se lo describe como default on. El registro dice ON sin mencionar el cambio.
- `DEFER_DRAW_TRANSITIONS` (Descartado): El registro lo da con defecto on, pero esa rama (p2-transiciones) no se integró y el interruptor no existe en la rama actual (2026-11-03).
- `FRONT_PREP_WORKER` (Descartado): El A/B del 2026-10-23 daba estática +0,89 con solo draws (commit 42d29e8f) y el mismo día 02768616 midió 27,83→25,51 con el worker activo; las cifras de estática son contradictorias entre tandas.
- `FRONT_PREP_WORKER_COMPUTE` (Descartado): La incoherencia del registro se explica por el commit 42d29e8f, que cambió el defecto de 6b9f6362 (=0 quitaba dispatches) a apagado (=1 los añade).
- `STAMP_ON_CHANGE` (Descartado): Un informe del agente (2026-11-05/06) proponía STAMP_ON_CHANGE=1 por defecto condicionado a dc19/dc17<dc18; el registro lo da por defecto 0 y Descartado. El motivo final solo consta como neutro.
- `RENDER_WORKERS` (ON): El registro dice que con RENDER_PIPELINE=1 no se usan; en gta/parres FRONT_SLICES=1 sí los usa como workers de grabación de slices.
- `RESOLVE_WORKERS` (ON): El registro dice «sin medir»; sí hubo medidas: gz05 (sin efecto en FPS) y las ramas y2a/y2b/y2c de FRONT_RESOLVE_POOL.
- `SLICE_STENCIL_PLANE` (OFF): El registro dice «sin medir», pero sd1 (SLICES + STENCIL_PLANE) dio render 122,2 ms vs 136,0 de la base sin slices.
- `SLICE_THREAD_INDIRECT` (OFF): El registro dice «sin medir»; sbt lo midió: 128 ms vs 133 de sbb.
- `SDL_AUTO_JOYSTICKS` (ON): El informe del agente (2026-10-17) dice que los interruptores no quitan nada en paneo y que el FPS de paneo bajó de 20,76 a 19,57; el registro cita paneo +0,52 (n=5) del brazo J de otra tanda.
- `CHILD_ASLR` (ON): El registro cita paneo 17,88 vs 19,01 y fecha 2026-10-18; la medición final del A/B (2+2, 17-10) fue neutra (24,21 vs 24,11) y el -1,1 previo se atribuyó a ruido; el commit bc02c288 es del 17-10.
- `CPU_DIRTY_SKIP` (ON): El registro dice «sin A/B de FPS»: existe el A/B del paquete de recortes (16,23 vs 15,74, +0,49), pero no aislado. Tras la fusión del 03-11 el interruptor se sigue leyendo y se endureció (CPU-sucia y escribible).
- `GC_GEN0_MB` (ON): El 17-10, ya con 64 MB, aún había pausas de ~41 ms (23 gen0 + 9 gen1 en 12 s, 4,5 ms/frame), lo que llevó a GC_SERVER.
- `HLE_SPAN_WRITES` (ON): El primer A/B del 15-10 (arm M) dio estática 20,23 vs 20,90 (peor) porque el juego enviaba antes y las esperas de GPU se duplicaban; el +3-4 FPS del registro es de integrar con las esperas arregladas el 17-10.
- `LEDGER_SNAPSHOT` (ON): Tras la fusión de origin/main (03-11) nuestro Publish/Snapshot se descartó a favor del PermissionRangeTree de upstream; =0 ya no restaura el comportamiento original sino el cerrojo sobre el árbol vivo.
- `PRERESERVE_MIN` (ON): El registro lo presenta como el arreglo; el A/B de 13 sesiones no distinguió brazos (0 caídas en ambos, regiones ajenas crecían a 41–58 en los dos) y «ayuda solo marginalmente»; el arreglo real fue CHILD_ASLR=low.
- `RECORDER_KEYSORT` (ON): El registro marca la incoherencia 3,4 vs 5,4 ms: el commit 04380721 dice 5,4 ms/frame; la cifra de 3,4 del comentario no aparece en las transcripciones.
- `SINGLE_VIEW_WRITES` (ON): El primer A/B del 15-10 (arm M) fue peor en estática (20,23 vs 20,90) porque las esperas de GPU se duplicaban; el +3-4 FPS del registro es de la integración del 17-10.
- `SPAN_SNAPSHOT` (ON): El registro dice «sin A/B de FPS», pero el informe del 12-oct da un A/B +0,49 FPS para el lote completo (no aislado).
- `STRIPED_ACCESS_COUNT` (ON): El 15-oct el brazo M (con este interruptor) dio estática 20,23 vs 20,90 de la base y se decidió mantenerlos apagados; el registro solo cita el +3-4 FPS posterior.
- `FAST_IMPORT_GATEWAY` (OFF): El registro dice «sin medir»; solo se midió dentro del brazo H, mezclado con CHUNKS y los interruptores de memoria.

## Por interruptor

### GPU/readbacks (77)

<a id="async_guest_readback"></a>
#### `ASYNC_GUEST_READBACK` — ON, def. on

Razonamiento: Es la lectura asíncrona y anticipada de memoria escrita por GPU de upstream (#1013): el hilo del guest espera la copia en su propio hilo y el render solo la aplica. Se usó =0 (con ASYNC_READBACK=0) únicamente para ver si una carrera del readback asíncrono en páginas con escritores de CPU y GPU explicaba los giros del BVH con last_node=-1; sin veredicto de rendimiento propio. *[A/B-upstream]*

Fuentes: 3d7ca311 2026-10-09, commit 6277441e

<a id="async_readback"></a>
#### `ASYNC_READBACK` — ON, def. on

Razonamiento: Cola de readback propia de upstream. =0 elimina esa segunda cola y solo se usó como diagnóstico: contra carreras de readback en el BVH (rt112int) y como una de las vías que eliminan un cuelgue en el que la copia quedaba tras un envío sin sellar (se prefirió READBACK_SUBMIT_GUARD=1, que conserva la cola). *[estabilidad]*

Fuentes: 3d7ca311 2026-10-09, 3d7ca311 2026-10-12

<a id="async_readback_retries"></a>
#### `ASYNC_READBACK_RETRIES` — ON, def. 3

Razonamiento: Cerca del 20 % de las lecturas asíncronas del guest quedaban obsoletas porque la GPU las sobrescribía antes de aplicarlas y se rehacían síncronas en la cola GPU (~2,4 ms cada una, ~3,6 de los 6,8 ms de espera del render por fotograma). El hilo del guest reintenta él mismo hasta 3 veces antes de caer a la vía síncrona; se encendió porque bajó la espera de GPU y subió algo el FPS. *[ganancia]*

Fuentes: 3d7ca311 2026-10-10, 3d7ca311 2026-10-11, commit f761dd7e

<a id="bda_fault_window_bytes"></a>
#### `BDA_FAULT_WINDOW_BYTES` — ON, def. 4096

Razonamiento: Upstream trae una ventana alineada de 2 MiB por cada fallo de dirección de dispositivo; la rama previa subía solo la página fallada. Se creó para descartar ese default como causa de la regresión de la fusión (fz27 no cambió: 4,78 FPS) y se fijó a 4096 por el principio de Daniel (defecto = lo nuestro). *[A/B-upstream]*

Daniel: Principio de Daniel (2026-11-04): donde upstream cambió el comportamiento y lo nuestro funciona en GTA, interruptor SHARPEMU_* con defecto = lo nuestro (3368666f) y lo de upstream como opt-in.

Fuentes: 3d7ca311 2026-11-04, commit 6b1ad122, commit c09e3f5b

<a id="bda_scan_mapped"></a>
#### `BDA_SCAN_MAPPED` — ON, def. 1

Razonamiento: El escaneo del bitmap de fallos BDA costaba 3x150 µs de GPU por fotograma. El primer diseño (solo gránulos con spans registrados) perdía fallos en páginas nunca mapeadas y GTA cayó a 9 FPS; el diseño final marca un bitmap resumen por gránulo de 32 MiB, exacto por construcción. Se aceptó por mecanismo (-0,42 ms de GPU): el FPS quedó en el ruido (el +3,4 de paneo, probablemente ruido). *[ganancia]*

Fuentes: 3d7ca311 2026-10-29, commit 415deeea, commit e76f182f

<a id="buffer_int64_atomics"></a>
#### `BUFFER_INT64_ATOMICS` — ON, def. on

Razonamiento: El refit del BVH (SWAP_X2/OR_X2 y cargas GLC de 2 o más dwords) necesitaba átomos de 64 bits en búferes: es el arreglo de la PR 27 (shaderBufferInt64Atomics). El =0 se dejó para A/B y el A/B en el juego mostró que importa: apagado 2,94 FPS con el refit al 77 % de la GPU, encendido 6,60 FPS. *[corrección]*

**Contradicción:** El registro dice que el interruptor solo existe para A/B sin cifras; sí hubo A/B en el juego (2,94 vs 6,60 FPS).

Fuentes: 3d7ca311 2026-10-09, 3d7ca311 2026-10-29

<a id="completion_poll_us"></a>
#### `COMPLETION_POLL_US` — ON, def. 100

Razonamiento: Antes de cada draw/dispatch se leía el semáforo timeline y se corría el trabajo diferido (1.640 sondeos, ~1,7 ms/fotograma). Se limitó a uno cada 100 µs (con la cola llena sigue recogiendo al instante); aceptado por mecanismo con el FPS en ruido. En la fusión upstream traía un 250 µs fijo y se conservó el intervalo propio; el 6-nov se sugirió probar 250 porque la limpieza aún cuesta hasta 3 ms/fotograma. *[ganancia]*

Fuentes: 3d7ca311 2026-10-11, 3d7ca311 2026-11-03, 3d7ca311 2026-11-06, commit 94efc329

<a id="cp_write_clean"></a>
#### `CP_WRITE_CLEAN` — ON, def. on

Razonamiento: F5EB y 4481 esperaban lecturas síncronas de palabras que escribe el CP (WRITE_DATA), que deja iguales backing y dispositivo, pero seguían en el conjunto GPU-modificado por marcas conservadoras y cada lectura descargaba esperando al lote. Se sacan esos bytes del conjunto (exacto, 59 % menos de lecturas a sincronizar). Solo no ganaba FPS (se dejó apagado), pero entró por defecto dentro del paquete genérico G junto a DEVICE_DATA_READS y PRECISE_TABLE_READ. *[adelanta-cuello]*

Fuentes: 3d7ca311 2026-11-01, commit 5fd7219d, commit a2431729

<a id="deferred_protect"></a>
#### `DEFERRED_PROTECT` — ON, def. on

Razonamiento: Reproteger las páginas subidas (UpdateCpuProtection/TryProtect) costaba ~3 ms/fotograma en el hilo de render, y ya se había visto que resubir sale mucho más caro (BDA_PRESERVE_HOT). Se movió la reprotección y la copia al staging a un worker, uniendo antes de cada submit; criterios previos: exacto (capturas idénticas, BLAS sin ciclos) y abandonar si la unión espera más de ~1 ms. Resultado 4,1 a 0,7 ms y +0,35 FPS, activado por defecto. *[ganancia]*

Fuentes: 3d7ca311 2026-10-11, commit f1341249

<a id="deferred_protect_workers"></a>
#### `DEFERRED_PROTECT_WORKERS` — ON, def. 2

Razonamiento: Con 2 workers la espera del render bajó de 2,0 a 1,1-1,35 ms, pero el FPS no mejoró (14,06 vs 14,29): el tiempo se movió a otras esperas y se dejó en 1. El 31-10 se arregló la unión por orden de trabajo para que >1 fuese exacto, y 2 workers + RECORD_RING_WAKE=64 dieron +0,6-0,8 FPS en paneo, aunque en el segundo A/B la ganancia baja a +0,1 al excluir una sesión base atípica (26,55). *[adelanta-cuello]*

**Contradicción:** El registro da +0,6-0,8 FPS; el informe del 31-10 indica que en el segundo A/B la ganancia baja a +0,1 si se excluye una sesión base de 26,55.

Fuentes: 3d7ca311 2026-10-11, 3d7ca311 2026-10-31, commit 49844e25, commit fa51b8c0, commit fd290588

<a id="deferred_upload_wait_block"></a>
#### `DEFERRED_UPLOAD_WAIT_BLOCK` — ON, def. on

Razonamiento: El perfil de contención mostró el proceso a ~603 % de una CPU lógica, con la unión de subidas diferidas del hilo de envío girando (~1,8 ms/fotograma) además del anillo de grabación. Girar decenas de µs y bloquear en un monitor entró por defecto dentro del paquete de contención (brazo L: -50 core-ms/fotograma) por el criterio por ms, aunque el FPS quedó en ruido (estática +0,40, paneo -0,13). *[ganancia]*

**Contradicción:** (resuelve la incoherencia del registro) 80b6abbf lo creó apagado, pero el mismo 16-10 pasó a encendido (el código lee != «0» según el parche de ese día).

Fuentes: 3d7ca311 2026-10-16, commit 80b6abbf

<a id="device_data_reads"></a>
#### `DEVICE_DATA_READS` — ON, def. auto

Razonamiento: El hilo de render esperaba ~5-6 ms/fotograma en vaciados por lecturas síncronas de la cadena del TLAS (F5EB, 4481, 994A...), porque los S_LOAD se aplanaban a una tabla del host. Con una lista de hashes, los S_LOAD de solo datos los lee el shader por la tabla de páginas; la lección fue que quitar una lectura solo adelanta la siguiente, así que hubo que vaciar la cadena entera. =auto lo generaliza sin hashes (paquete G), exacto (0 discrepancias en 1,8e9 lecturas). *[adelanta-cuello]*

Fuentes: 3d7ca311 2026-10-31, 3d7ca311 2026-11-01, commit 8512be4e, commit a2431729, commit e1c66606

<a id="eager_mute"></a>
#### `EAGER_MUTE` — ON, def. on

Razonamiento: Idea de upstream #1082 (dada4dff) con implementación propia: una ventana cuya descarga anticipada caduca 3 veces seguidas no se adelanta en 4096 lotes. Solo actúa con EAGER_READBACK=1, que está apagado, así que es inerte por defecto; en la fusión se adoptó la cuenta de strikes de upstream y =0 sigue apagándolo. *[sin-A/B]*

Fuentes: 3d7ca311 2026-10-12, 3d7ca311 2026-11-03, commit 158d0baf

<a id="ever_written_fast"></a>
#### `EVER_WRITTEN_FAST` — ON, def. on

Razonamiento: Uno de los recortes exactos por draw del render (gta/perdraw, -2,06 ms/fotograma de código gestionado en conjunto): el único hilo que añade al conjunto ever-GPU-written consulta Contains sin cerrojo. Si apareciera un segundo escritor se apaga solo con un aviso; tras la fusión se anotó que solo es seguro mientras NoteEverGpuWritten no se llame fuera del hilo de render. *[ganancia]*

Fuentes: 3d7ca311 2026-10-25, 3d7ca311 2026-11-03, commit c5813da2

<a id="fast_fill"></a>
#### `FAST_FILL` — ON, def. on

Razonamiento: CommandMemoryTransfer (~5 ms/fotograma) eran rellenos DMA de ~21 MB/fotograma (~1 GB cada 5 s, limpiezas de targets) aplicados como memset de CPU en trozos de 4 KB sobre memoria que ningún búfer ni imagen de GPU cubre aún. Primero salió opcional y sin ganancia porque no alcanzaba esos rellenos; luego se vio que mandaba el coste por llamada (~5.000 llamadas/fotograma) y con trozos de 256 KB bajó a 3,4 ms y se encendió. *[ganancia]*

**Contradicción:** (resuelve la incoherencia del registro) f7ab3b87 lo dejó opcional sin ganancia (trozos de 4 KB) y el mismo día 2fc1fe33 lo encendió con trozos de 256 KB.

Fuentes: 3d7ca311 2026-10-10, commit 2fc1fe33, commit f7ab3b87

<a id="fault_read_flush"></a>
#### `FAULT_READ_FLUSH` — ON, def. on

Razonamiento: Con FAULT_READ_EMULATE, la lectura emulada no enviaba el lote en grabación que contiene al escritor, como sí hace la descarga normal; se sospechó que ese envío era un efecto colateral del que dependía el guest (sin él el front espera un REWIND que el guest parchea tras su sondeo). Encenderlo no cambió nada (21,8 vs 22,8 FPS): la lentitud de la emulación no venía de ahí. Solo aplica con EMULATE. *[exacto-neutro]*

Fuentes: 3d7ca311 2026-10-26, commit 04e903f1

<a id="obtain_fast_checks"></a>
#### `OBTAIN_FAST_CHECKS` — ON, def. on

Razonamiento: El obtain de buffers hace unas 11.000 comprobaciones de suciedad por fotograma; se evalúa el mismo predicado con el orden más barato primero (resumen sin cerrojo y GPU-dirty solo si hace falta), tras un interruptor para A/B. Los texel buffers se dejaron como estaban porque pueden depender de imágenes con alias. El FPS quedó en ruido pero el coste por llamada baja y es exacto, así que se queda. *[ganancia]*

Fuentes: 3d7ca311 2026-10-11, commit 36bc6c40

<a id="parallel_fill"></a>
#### `PARALLEL_FILL` — ON, def. on

Razonamiento: Los rellenos grandes (≥2 MB) escribían la copia guest en un solo hilo; se repartieron en trozos de 256 KB entre núcleos y se encendió por defecto tras medirlo (rt421). Está limitado por el ancho de banda de memoria. *[ganancia]*

Fuentes: 3d7ca311 2026-10-10, commit ed4e6355

<a id="precise_table_read"></a>
#### `PRECISE_TABLE_READ` — ON, def. 1

Razonamiento: Las lecturas síncronas de la cadena del TLAS (F5EB, 4481, 994A) esperaban al tick del bloque de 16 KB porque los rangos escritos conservadores de ~40 MB (10CD, 6A53, 8395) cubren sus V#; se probó esperar solo al último escritor de esos bytes. Con lista de hashes daba 100 % de fallbacks (77 % con el escritor sin enviar, 23 % en vuelo) aunque el verify daba 0 discrepancias; generalizado a todas las palabras (WORDS=all) en el paquete genérico bajó la espera de la cadena y se encendió por defecto. *[ganancia]*

Fuentes: 3d7ca311 2026-10-31, commit 4c41ae9d, commit a2431729, commit a9b95c25

<a id="precise_write_ticks"></a>
#### `PRECISE_WRITE_TICKS` — ON, def. 1

Razonamiento: Las lecturas síncronas esperaban al tick del bloque entero; un anillo con todas las escrituras GPU en direcciones guest da a cada rango su escritor real y la lectura espera solo a ese. Es exacto, pero el 14-10 salió neutro (estático −0,40, giro +0,52) porque el fotograma estaba limitado por latencia (1 ms recortado del render movía solo ~0,45 ms); se dejó opcional, a re-probar con un bucle más rápido, y se encendió el 03-11 junto a TABLE_WORD_MEMO_LEARN y AUDIT. *[exacto-neutro]*

Daniel: Según el resumen de compactación del 15-10, Daniel aceptó dejar los cambios exactos pero neutros (entre ellos este) para re-probarlos cuando el bucle del juego fuera más rápido.

Fuentes: 3d7ca311 2026-10-14, 3d7ca311 2026-10-15, commit 6833c8b7

<a id="range_write_ticks"></a>
#### `RANGE_WRITE_TICKS` — ON, def. on

Razonamiento: Los blobs BVH y otros búferes grandes los escribe casi cada dispatch, así que con un tick por búfer casi todo readback esperaba al lote en grabación. Con un tick por bloque de 16 KB solo se espera al escritor de esos bytes (readbacks que esperan el lote actual ~1.450 → ~425), correcto (0 blobs con ciclo) y se encendió por mecanismo aunque el FPS queda en ruido. La fusión 764260b4 adoptó además el WriteTickMap de upstream para ticks exactos por rango. *[ganancia]*

Fuentes: 3d7ca311 2026-10-10, commit 07059338, commit 764260b4

<a id="readback_pieces"></a>
#### `READBACK_PIECES` — ON, def. on

Razonamiento: La descarga asíncrona era todo o nada: si la GPU reescribía cualquier pieza mientras bajaba, se descartaba el lote entero y se reintentaba (~680 reintentos por 5 s en GTA V). Ahora cada pieza no reescrita se publica y solo las reescritas siguen GPU-modificadas; entró por defecto en el lote de revisión del 12-10 (conjunto +0,4 FPS, imagen idéntica) y en la fusión se conservó nuestra versión frente a la de upstream. *[sin-A/B]*

Fuentes: 3d7ca311 2026-10-12, 3d7ca311 2026-11-03, commit 47f4c521

<a id="readback_window"></a>
#### `READBACK_WINDOW` — ON, def. 65536

Razonamiento: La auditoría mostró que casi todos los readbacks son de 4–16 B pero cada uno traía una ventana de 512 KB; se hizo configurable para medir si la copia pesaba. 4 KB duplica los readbacks y pierde ~0,9 FPS, 64 KB empata con 512 KB y se fijó por defecto el 22-10. La conclusión fue que la ventana no es palanca: el ahorro real exige no esperar, no copiar menos. *[otro]*

Fuentes: 3d7ca311 2026-10-11, 3d7ca311 2026-10-22, commit 6b806e31, commit 95fb30ad

<a id="scope_barrier_prune"></a>
#### `SCOPE_BARRIER_PRUNE` — ON, def. 2

Razonamiento: Cada BeginRendering registra una barrera global para que un flush diferido al final del ámbito no deje escrituras sin ordenar; un rastreador marca las escrituras sin ordenar (bindings escritos, rangos de device-address, imágenes de almacenamiento, transferencias) y con =2 omite la barrera si no queda ninguna. Poda el 88 % de las pasadas y ahorra ~1,1 ms de GPU (+0,5 FPS en estático, nada en paneo) sin hazards nuevos en syncval. El 07-11 se corrigió que la barrera de un worker de slices borraba el estado del hilo principal. *[ganancia]*

Fuentes: 3d7ca311 2026-10-17, 3d7ca311 2026-11-07, commit bd29f523, commit db8d1fc6, commit e859f413

<a id="stencil_association_map"></a>
#### `STENCIL_ASSOCIATION_MAP` — ON, def. on

Razonamiento: Borrar una imagen de profundidad recorría todos los slots buscando asociaciones de stencil (~31 µs por borrado en GTA V, creciendo con la sesión). Idea tomada de Kyty-GTA 929dd914 con implementación propia: un mapa profundidad→asociaciones, re-apuntado al mover el rango. Entró en el lote de revisión del 12-10 (11 commits, +0,4 FPS conjunto, imagen idéntica). *[ganancia]*

Fuentes: 3d7ca311 2026-10-12, commit ee81dd3a

<a id="stencil_layer_clear"></a>
#### `STENCIL_LAYER_CLEAR` — ON, def. on

Razonamiento: GTA V limpia el stencil de su depth D32S8 de sombras (1024×1024, 6 capas) una capa de 1 MB cada vez con un record fill, dibujando esa capa entre medias. Como el fill no cubría el plano entero iba por memset de CPU, el plano quedaba sucio y AcquireDepthTargetView lo recargaba (6 MB) antes de cada uno de los 6 pases (~2,3 ms de lectura por fotograma). Limpiar solo esa capa en la GPU dio 14,29/14,53/14,60 → 15,42/15,03/15,36 FPS con la misma imagen. *[ganancia]*

Fuentes: 3d7ca311 2026-10-11, commit eec7cdbc

<a id="store_table_sync"></a>
#### `STORE_TABLE_SYNC` — ON, def. 1

Razonamiento: La tabla de 2 MB 0x55F883630 quedaba GPU-sucia en paneo porque 10CD y 6A53 enlazan como escrito un V# que apunta dentro de ella, elegido con un waterfall y sin rango exacto: es una escritura posible real, no un falso positivo de seguimiento. En la práctica sus bytes no cambiaron en ~1.400 descargas, pero solo una descarga lo demuestra, así que no se puede saltar la sincronización; el coste venía de que cada descarga esperaba en la cola de readback (~5,7 ms), no del escritor. Se sincroniza el trozo entero por una segunda cola. *[ganancia]*

Fuentes: 3d7ca311 2026-10-25, commit b1bd2684, commit d1d954a8

<a id="strict_compute"></a>
#### `STRICT_COMPUTE` — ON, def. on

Razonamiento: Interruptor de upstream (programa compute rechazado es fatal). Se probó =0 en rt91int para ver si saltarse el programa desbloqueaba la ejecución: peor, ErrorDeviceLost (TDR) en el tick 18566 porque omitir el programa deja a otro shader girando en la GPU. Descartado como vía. *[estabilidad]*

Fuentes: 3d7ca311 2026-10-09, commit 6b7c1d50

<a id="touch_once_per_tick"></a>
#### `TOUCH_ONCE_PER_TICK` — ON, def. on

Razonamiento: Idea tomada de Kyty-GTA c0c4f52a con implementación propia: la imagen guarda el tick en que se tocó su entrada de recencia y la cola LRU solo se toca si es más vieja, para no cargar esa entrada (otro objeto) en los miles de toques por fotograma. El orden de la cola no cambia; entró en el lote de revisión del 12-10 sin cifra propia. *[sin-A/B]*

Fuentes: 3d7ca311 2026-10-12, commit db3e9a76

<a id="da_host_backing_block_mb"></a>
#### `DA_HOST_BACKING_BLOCK_MB` — OFF, def. 256

Razonamiento: Parámetro (256 MiB por defecto) de la importación del backing del guest como memoria de host (VK_EXT_external_memory_host) del experimento DA_HOST_BACKING, en la rama gputables que quedó aparcada. Con 256 MiB salieron 29 bloques (7,4 GiB fijados en RAM, ~4,4 s de importación en el arranque); no hay medida del propio parámetro. *[sin-A/B]*

Fuentes: 3d7ca311 2026-10-18, commit cdd279dc

<a id="image_state_generation"></a>
#### `IMAGE_STATE_GENERATION` — OFF, def. off

Razonamiento: Es el contador de generación de estado de imágenes que necesita ATTACH_REPEAT (IsRepeatableTarget/TargetStateVersion); el interruptor solo fuerza el conteo cuando ATTACH_REPEAT está apagado (p. ej. para A/B o verify). ATTACH_REPEAT está activo por defecto desde 20308bde (-0,5 ms/fotograma en acquire, 0 discrepancias en modo 2), así que el conteo ya corre sin este. *[sin-A/B]*

Fuentes: 3d7ca311 2026-10-18, 3d7ca311 2026-10-22

<a id="mapped_buffers"></a>
#### `MAPPED_BUFFERS` — OFF, def. off

Razonamiento: Upstream usa búferes del huésped device-local mapeados (ReBAR). Tras la fusión (~4,7 FPS) se sospechó de este y otros defaults de upstream: fz24 con todos puestos a lo nuestro (MAPPED_BUFFERS=0, workers=0, batch barriers=0, SDL=0) dio 4,80 FPS, y gz05 mostró que los seis defaults pendientes no mueven el FPS (27,29 vs 26,56; con todos lo nuestro 27,16). Se dejó =0 por el principio de Daniel (defecto = lo nuestro, upstream opt-in). *[A/B-upstream]*

Daniel: Principio de Daniel (2026-11-04): donde upstream cambió el comportamiento y lo nuestro funciona en GTA, interruptor SHARPEMU_* con defecto = lo nuestro (3368666f) y lo de upstream como opt-in.

**Contradicción:** El registro dice «sin A/B propio»; hubo A/B conjuntos (fz24 con otros defaults; gz05/gz10 de los seis defaults) pero ninguno aislado.

Fuentes: 3d7ca311 2026-11-04, commit 3fe66d3f, commit bab71afa

<a id="readback_guest_submit"></a>
#### `READBACK_GUEST_SUBMIT` — OFF, def. off

Razonamiento: QueueRelay es casi todo los dos saltos de relevo de las lecturas del guest de memoria escrita por la GPU; se probó que grabe y envíe la copia el propio hilo del juego en vez del hilo de cola GPU. Reduce el inicio (51,7 → 7,9 µs) pero sube las lecturas por fotograma un 40 % y not_clean ×4, así que se devolvió a revisar exactitud y reintentos; mover la copia al backing fuera del render se descartó por inseguro (otros hilos verían la página desprotegida antes de los bytes). *[otro]*

Fuentes: 3d7ca311 2026-11-07, commit fb9c406b

<a id="acquire_barrier"></a>
#### `ACQUIRE_BARRIER` — Descartado, def. off

Razonamiento: Tras la fusión con upstream GTA V cayó a ~4,8 FPS y se sospechó de la barrera global que upstream emite en ACQUIRE_MEM con acciones de caché. Con la barrera quitada (fz27) no cambió nada, así que no era la causa; se dejó =0 por el principio de Daniel (defecto = lo nuestro, upstream opt-in). *[A/B-upstream]*

Daniel: Principio de Daniel (2026-11-04): donde upstream cambió el comportamiento y lo nuestro funciona en GTA, interruptor SHARPEMU_* con defecto = lo nuestro (3368666f) y lo de upstream como opt-in.

Fuentes: 3d7ca311 2026-11-04, commit 6b1ad122, commit c09e3f5b

<a id="bda_preserve_hot"></a>
#### `BDA_PRESERVE_HOT` — Descartado, def. off

Razonamiento: La re-protección de páginas tras subir (TryProtect desde el barrido PrepareBda) costaba ~2,7 ms/fotograma, y se probó dejar escribibles y sucias las páginas calientes para evitarla. Salió a 8,5 FPS: cada bind vuelve a transmitirlas (~6 MB/fotograma repetidos). Lección: proteger cuesta mucho menos que resubir; el camino es abaratar TryProtect, y el coste real está en el handler de fallos (~32 µs cada uno). *[otro]*

Fuentes: 3d7ca311 2026-10-11, 3d7ca311 2026-10-18, commit 46e639b8

<a id="bda_sweep_plan"></a>
#### `BDA_SWEEP_PLAN` — Descartado, def. off

Razonamiento: Tras la fusión, el plan de barrido por páginas de upstream (bloque a bloque bajo el cerrojo de región) tardaba 1,45 ms/fotograma en solo 2-3 barridos. Se dejó por defecto el recorrido por spans sucios de 3368666f (exacto, 0,30 ms), que fue lo que más ayudó a recuperar la paridad en esa ronda (estática 27,3 a 29,26, paneo a 27,1-27,7). *[A/B-upstream]*

Fuentes: 3d7ca311 2026-11-04, commit db7b07ca

<a id="clean_byte_reads"></a>
#### `CLEAN_BYTE_READS` — Descartado, def. 0

Razonamiento: Variante para que una lectura de tabla sobre bytes no modificados por GPU no descargara su ventana (=2 verify). Nació en la misma rama que CP_WRITE_CLEAN y, con este activo, nunca llega a activarse (0 lecturas), así que no aporta y se dejó apagada. *[exacto-neutro]*

Fuentes: 3d7ca311 2026-11-01, commit 5fd7219d

<a id="clean_read_faults"></a>
#### `CLEAN_READ_FAULTS` — Descartado, def. off

Razonamiento: El RenderThread del juego perdía ~15 ms/fotograma esperando en fallos de lectura de 8 bytes sobre la página de etiquetas EOP (0x140900000), que quedaba GPU-sucia por bindings pequeños de cómputo aunque la GPU no escribía esos bytes. Se sirvieron 128.000 lecturas desde la copia de CPU, pero la espera solo se movió (hilo 59 al hilo principal) y el FPS no subió; se cerró la línea sin integrar. Ya se sabía que un guest más rápido no se había convertido en FPS antes (interruptores M). *[adelanta-cuello]*

Fuentes: 3d7ca311 2026-10-16, commit 1bd5cf85

<a id="conservative_records"></a>
#### `CONSERVATIVE_RECORDS` — Descartado, def. 0

Razonamiento: La palabra 5981+0x60 (num_records de la lista de nodos del refit, también nº de hilos del dispatch indirecto) se leía síncrona: ~11 ms por lectura por una falsa dependencia del bloque de 16 KB (su escritor real F6F2 ya había acabado; AACC escribía al lado). =1 (centinela hasta fin de búfer, 21 MB) pierde FPS; =3 (esperar solo al escritor preciso) es exacto y baja esa espera, pero la espera se desplaza a B198, que lee lo de 5981 en el mismo lote, y el FPS no mejora. Quedó opcional al cubrirlo PRECISE_TABLE_WORDS=all. *[adelanta-cuello]*

Fuentes: 3d7ca311 2026-10-30, 3d7ca311 2026-10-31, commit 9f5c7738, commit d3f2a6ff

<a id="cut_policy"></a>
#### `CUT_POLICY` — Descartado, def. 0

Razonamiento: Los hilos del juego (Subrender x4, RenderThread) esperaban 13-25 ms por fotograma sobre el bloque de 64 KB 0x59C900000 (StoreDownload) porque los lotes se enviaban tarde; se probó cortar el lote tras el escritor de la palabra leída, en ventanas de 64 KB o por duración. X (CUT=3+PWT+LEARN+AUDIT) dio 29,70 vs 28,93, pero la atribución mostró que lo que gana es PRECISE_WRITE_TICKS+LEARN: sin cortes (Y) sale 29,82. No hace falta. *[exacto-neutro]*

Fuentes: 3d7ca311 2026-11-02, commit 4f4ed844

<a id="da_host_backing_create_wait"></a>
#### `DA_HOST_BACKING_CREATE_WAIT` — Descartado, def. off

Razonamiento: Sonda de la investigación de 0x260F, programa que rompía el BVH al ejecutarse en el dispositivo: hacer que crear un búfer espere al lote más nuevo de direcciones de dispositivo. Ni esta ni las demás sondas de esa serie impidieron la rotura y la rama gputables se aparcó. *[otro]*

Fuentes: commit d5062a3d

<a id="da_host_backing_sweep"></a>
#### `DA_HOST_BACKING_SWEEP` — Descartado, def. off

Razonamiento: Programas que construyen V# en la GPU (4481, etc.) pueden apuntar a búferes registrados con escrituras de CPU sin subir, y PrepareBda solo subía los spans enlazados; el barrido sube los CPU-sucios de todas las vistas con backing (~0,46 ms/fotograma). Con modo 2 + barrido el refit queda sano (21,3 FPS, frente a 9,8 solo con modo 2), pero GpuCompletionWait no bajó (la espera se desplaza a F5EB/994A/AACC) y 0x260F seguía rompiendo el BVH: línea aparcada. *[adelanta-cuello]*

Fuentes: 3d7ca311 2026-10-18, commit 5349b339

<a id="deferred_hot_preserve"></a>
#### `DEFERRED_HOT_PRESERVE` — Descartado, def. off

Razonamiento: El 95 % de las ~1.000-1.250 páginas por fotograma que las subidas diferidas reprotegen ya eran calientes (cada una cuesta protect + fallo + unprotect), así que se probó dejarlas sucias y escribibles. Es exacto, pero cada obtain las copia otra vez: copias de 0,6 a 12 ms/fotograma y paneo ~19 vs ~24 FPS. Misma conclusión que BDA_PRESERVE_HOT. *[otro]*

Fuentes: 3d7ca311 2026-11-02, commit 44889f54

<a id="eager_readback"></a>
#### `EAGER_READBACK` — Descartado, def. off

Razonamiento: La lectura anticipada de ventanas calientes (camino de upstream #1013) forzaba un envío por cada escritura GPU a una ventana caliente: ~120 de ~155 envíos/fotograma. Apagarla subió de 6,26 a 6,64 FPS y un A/B posterior con ticks por rango lo confirmó (12,13 vs 11,13 FPS; QueueSubmit 1,6 a 12,3 ms con 139 envíos/fotograma), así que quedó apagada. *[otro]*

Fuentes: 3d7ca311 2026-10-09, 3d7ca311 2026-10-10

<a id="early_writer_submit"></a>
#### `EARLY_WRITER_SUBMIT` — Descartado, def. 0

Razonamiento: Las lecturas síncronas de tabla esperan un vaciado porque el escritor aún está en el lote abierto; se probó enviar el lote justo tras el dispatch que escribe la palabra. Solo convierte la espera de vaciado en espera parcial 1:1 y sin límite de tamaño perdía FPS (18,46 vs 18,68). El modo 2 (solo palabras cuya última lectura cambió) se añadió porque las palabras que la GPU nunca cambia (0xF43B, 0x994A) lo estropeaban; tampoco gana. *[adelanta-cuello]*

Fuentes: 3d7ca311 2026-10-12, 3d7ca311 2026-10-13, 3d7ca311 2026-11-01, commit 6833c8b7, commit f766d383

<a id="fault_read_emulate"></a>
#### `FAULT_READ_EMULATE` — Descartado, def. 0

Razonamiento: El RenderThread del juego sondea una etiqueta CP WRITE_DATA (0x59C900000) en una página compartida con rangos escritos por GPU (10CD/6A53), así que cada fallo descargaba la ventana y esperaba a los escritores aunque los bytes no fueran GPU-modificados (~400 ms/s; cadenas de 2,5-2,8 fallos por lectura). Emular la carga desde el backing es exacto (=2: 0 discrepancias) y elimina las cadenas, pero hunde la estática a 19,8-22,9 FPS; hipótesis sin confirmar: el juego regula su ritmo con la etiqueta y ver progreso antes de que la GPU lo ejecute le hace esperar en otro sitio. *[adelanta-cuello]*

Fuentes: 3d7ca311 2026-10-22, 3d7ca311 2026-10-26, commit 1d10170d, commit 775ed6d3

<a id="fault_read_park_min_us"></a>
#### `FAULT_READ_PARK_MIN_US` — Descartado, def. 0

Razonamiento: El camino ordinario respondía al sondeo de la etiqueta con ~10 ms de retraso y el progreso en pasos grandes; se probó imitarlo con un suelo de aparcado en las lecturas emuladas (el juego regularía su ritmo con la etiqueta). 4000 µs dio 16,4 FPS: experimento de ritmo nulo, no recupera los ~29 FPS. *[otro]*

Fuentes: 3d7ca311 2026-10-26, commit 04e903f1

<a id="fault_write_granule"></a>
#### `FAULT_WRITE_GRANULE` — Descartado, def. off

Razonamiento: Upstream #1130 (8ddf6601) marca el gránulo de 16 KB entero en cada fallo de escritura del juego y NoteOtherWrite tira todas las palabras memoizadas del gránulo (memo de 154 a 93 palabras; 135-270 descartes por ventana vs 0), lo que multiplicaba por ~7 las lecturas síncronas del front (708 de 804 con el valor sin cambiar). Por defecto el fallo marca solo sus bytes (como 3368666f): espera del front de 79,6 a 10,7 ms por ventana; el riesgo (fallos de escritura 90k a 147k) no se vio en FPS. *[A/B-upstream]*

Fuentes: 3d7ca311 2026-11-04, commit dcc7371f

<a id="fill_on_gpu_min"></a>
#### `FILL_ON_GPU_MIN` — Descartado, def. 0

Razonamiento: Hipótesis: el coste real de los rellenos grandes era el memset de ~1 GB por ventana duplicado en copia guest y GPU, así que se enrutan como escrituras solo-GPU (marcando el rango GPU-escrito) a costa de un readback si la CPU lee después. Es correcto (rt412, 0 ciclos), pero los rellenos grandes caen fuera de todo búfer registrado y no los alcanza; la idea siguiente fue el relleno diferido. *[exacto-neutro]*

Fuentes: 3d7ca311 2026-10-10, commit f7ab3b87

<a id="flush_table_reads"></a>
#### `FLUSH_TABLE_READS` — Descartado, def. off

Razonamiento: El refit del BVH giraba hasta el guardián de 100k pasos (0,36 FPS) con +0x8C en 0x31C; se sospechó una carrera de readback (la palabra de tabla se leía sobre bytes GPU-sucios antes de que su escritor acabara) y se probó vaciar y esperar antes de leer. Correcto pero sin cambio: la carrera no era la causa (o no la única); el diagnóstico de ese momento apuntaba a un desbordamiento de la lista de nodos reconstruida. *[otro]*

Fuentes: 3d7ca311 2026-10-08, 3d7ca311 2026-10-09

<a id="heat_decay"></a>
#### `HEAT_DECAY` — Descartado, def. on

Razonamiento: Adaptación de a32988ae (foufouadi): una página que la CPU reescribe entre dos syncs se vuelve caliente (queda escribible y CPU-sucia, y se copia entera en cada sync) y nada la enfriaba; se enfría una vez por flip. En GTA no cambia las subidas (151 MB/s y 20,4k syncs/s con y sin decay), cuesta ~0,22 ms/fotograma y el FPS baja (24,29 vs 24,56): las páginas calientes lo están de verdad porque se reescriben cada fotograma. *[A/B-upstream]*

**Contradicción:** El registro marca Def: on, pero el cambio solo vive en la rama gta/heatdecay (no integrado) y está descartado.

Fuentes: 3d7ca311 2026-10-22, commit 7a8f0258

<a id="hostwrite_mark_dirty"></a>
#### `HOSTWRITE_MARK_DIRTY` — Descartado, def. off

Razonamiento: Hipótesis de las cabeceras BVH rotas: un WRITE_DATA que cae en un hueco entre búferes registrados no se marcaba CPU-sucio. La sonda (rt92int) mostró solo 3 escrituras de 4 B sin cubrir y ninguna de cabecera, y se descartó. Se reprobó tras la fusión (fz10, 3,81 FPS en un estado ya roto) sin efecto. *[otro]*

Fuentes: 3d7ca311 2026-10-09, 3d7ca311 2026-11-04

<a id="image_head_page_writes"></a>
#### `IMAGE_HEAD_PAGE_WRITES` — Descartado, def. off

Razonamiento: Nueve imágenes R8 de stencil (2,2 y 8,3 MB, ~15 recargas por fotograma) las escribe un hilo del juego cada fotograma y el borde vale siempre lo mismo; se probó desvigilar hasta 8 páginas con su hash y recargar solo si cambian. Los agujeros se agotan porque el juego escribe muchas páginas, y comparar todas exige releer la imagen, que es lo caro; ImageUpload igual (3,4 ms). *[otro]*

Fuentes: 3d7ca311 2026-10-10, commit e828ff78

<a id="image_publication"></a>
#### `IMAGE_PUBLICATION` — Descartado, def. off

Razonamiento: Tras la fusión, la publicación de imágenes escritas por GPU al búfer bajo ellas (upstream #1079/#1130) no existía en 3368666f y provocó un FATAL al publicar una textura en modo de tile de profundidad bajo un fill de RECORD_FILLS (fz34). Se dejó apagada por defecto (como la rama anterior) y, si se activa, avisa en lugar de cerrar el juego; en 9 sesiones posteriores no hubo fatales ni DeviceLost. *[A/B-upstream]*

Fuentes: 3d7ca311 2026-11-04, commit 2c8db06f

<a id="materialize_sync_validate"></a>
#### `MATERIALIZE_SYNC_VALIDATE` — Descartado, def. 0

Razonamiento: Una materialización cacheada cuyo rango podía ser aún de la GPU contaba como fallo y el recorrido completo leía las mismas palabras con una lectura síncrona cada una (F43B: ~6 readbacks por fotograma sobre cabeceras BLAS que no cambian). Se probó validar el rango en un único readback por lotes; es exacto pero gana a la par del ruido, así que queda apagado. Se aprendió que B198 y 02F1 son dependencias reales (la palabra cambia en cada lectura) y la espera se queda. *[exacto-neutro]*

Fuentes: 3d7ca311 2026-10-16, 3d7ca311 2026-10-17, commit 60e4edb6, commit fb7d1e34

<a id="native_state_reuse"></a>
#### `NATIVE_STATE_REUSE` — Descartado, def. 0

Razonamiento: Adaptado de la PR de upstream #1082 (saltar binds que repiten el estado ya enlazado); se dejó opt-in porque el efecto medido era ruido (hb02 +0,77 no se repitió, hb05 −0,41). En la fusión con upstream del 03-11 se verificó que se conservaba a 0 y en mundo abierto ±1 FPS lo hace indistinguible. *[A/B-upstream]*

Fuentes: 3d7ca311 2026-10-15, 3d7ca311 2026-11-06, commit 8d3ae108

<a id="no_sync_table_reads"></a>
#### `NO_SYNC_TABLE_READS` — Descartado, def. off

Razonamiento: Sonda para medir el techo de las esperas síncronas de tablas: devolver «no legible» da FATAL de materialización en 0x4481 y leer la copia rancia de CPU baja a 2 FPS porque las tablas malas hacen girar la GPU. Conclusión: esas esperas son necesarias para la corrección, porque entre las palabras hay dependencias reales (B198, 02F1) que sí cambian. *[otro]*

Fuentes: 3d7ca311 2026-10-09, 3d7ca311 2026-10-15

<a id="pending_metadata_fills"></a>
#### `PENDING_METADATA_FILLS` — Descartado, def. off

Razonamiento: La regla de upstream #1130 contaba las entradas PendingDcc de los fills como metadatos; los fills de cada cara de las cubemaps de sombra (512 KB) se escribían al backing y perdían el origen GPU-dirty, así que cada refresco hacía staging de la imagen entera. La causa se halló en la ronda 7 de la fusión poniendo una sonda idéntica en ambas ramas y comparando; se dejó a 0 como en 3368666f. *[A/B-upstream]*

Fuentes: 3d7ca311 2026-11-04, commit 01ca15e1

<a id="preserve_hot_obtain"></a>
#### `PRESERVE_HOT_OBTAIN` — Descartado, def. off

Razonamiento: Se midieron ~1.520 páginas por fotograma subidas y re-protegidas (97 % calientes) y ~230 fallos de escritura guest (~7,4 ms de manejador); la hipótesis era que dejar escribibles y sucias las páginas calientes en los obtains de lectura evitaba la re-protección. Solo quita ~8 % de los fallos y el FPS no cambia (24,50 vs 24,49), porque la re-protección masiva viene del barrido de direcciones de dispositivo; lo que pesa es el coste de cada fallo (~32 µs). *[otro]*

Fuentes: 3d7ca311 2026-10-18, commit c2b7b585

<a id="readback_mirror"></a>
#### `READBACK_MIRROR` — Descartado, def. 0

Razonamiento: Casi todas las lecturas pequeñas (4–16 B) del hilo de render esperan a la GPU; ~6 por fotograma tienen su escritor en el command buffer aún en grabación (~0,76 ms cada una). Se diseñó un espejo host-visible de líneas calientes de 64 B (tras Kyty 269c9e96) y un _FLUSH que envía pronto; es correcto, pero los envíos extra devuelven en grabación lo que quita de esperas (14,65 FPS apagado vs 14,4–14,56). Lección: el ahorro real exige no esperar, no copiar menos. *[adelanta-cuello]*

Fuentes: 3d7ca311 2026-10-11, commit 3f2c82f3, commit d66ab97a

<a id="readback_precise_wait"></a>
#### `READBACK_PRECISE_WAIT` — Descartado, def. off

Razonamiento: Las lecturas ≤256 B del render (2D34←91D5, 91D5←71AD) esperaban al tick del bloque de 64 KB; se probó bajar solo los bytes pedidos tras su escritor preciso. El verify da 0 discrepancias, pero la primera versión hizo 1,07 M lecturas precisas y perdió la agrupación por ventana (paneo 0,99 FPS); la segunda baja las piezas terminadas de la ventana. El 29-10 se vio que solo traslada la espera, porque en la cola única y en orden de la GPU hay más trabajo RT delante del escritor. *[adelanta-cuello]*

**Contradicción:** Hipótesis: el registro da estática 23,69 vs 23,85, pero el 23-10 el coordinador midió un control de 28,28 FPS y atribuyó los ~23,8 a la rama precisewait2 o a la instrumentación READBACK_WAIT_MS=1, así que esa comparación parece contaminada por la sonda.

Fuentes: 3d7ca311 2026-10-23, 3d7ca311 2026-10-29, commit 5cee4469, commit 876b18b0

<a id="readback_slots"></a>
#### `READBACK_SLOTS` — Descartado, def. 8

Razonamiento: Adaptado de la PR de upstream #1128 (TarkusR, 6d963114): subir los slots de readback de 8 a 32. En GTA V ninguna lectura encontró ocupados los slots (0 veces por fotograma con 8, en estática y paneo), así que no tiene nada que ganar; en la fusión del 03-11 se mantuvo 8 aunque upstream subió a 32. *[A/B-upstream]*

Fuentes: 3d7ca311 2026-10-29, 3d7ca311 2026-11-03, commit e76f182f

<a id="readback_slot_wait"></a>
#### `READBACK_SLOT_WAIT` — Descartado, def. off

Razonamiento: Adaptado de upstream #1128 (TarkusR): con todos los slots ocupados, esperar uno libre fuera de la cola GPU. GTA V nunca los encontró ocupados, así que queda apagado; en la fusión del 03-11 se mantuvo apagado aunque upstream espera siempre un slot. *[A/B-upstream]*

Fuentes: 3d7ca311 2026-10-29, 3d7ca311 2026-11-03, commit e76f182f

<a id="readback_submit_guard"></a>
#### `READBACK_SUBMIT_GUARD` — Descartado, def. off

Razonamiento: El cuelgue de la intro (~1 de cada 50–140 sesiones) dejaba al hilo de render en VulkanAsyncReadback.Wait mientras materializaba B198 y todos los ticks sellados entregados; la hipótesis era que 2484f3fa permitía enviar la copia de readback, que espera al timeline principal, antes de su señal. Se encendió la guardia el 12-10 (5a95d73f), pero el cuelgue se reprodujo con ella puesta (s1), así que se refutó y el 13-10 se devolvió a apagado añadiendo informes de stall; quitarla es neutra en FPS. *[estabilidad]*

Fuentes: 3d7ca311 2026-10-12, commit 2484f3fa, commit 512b3c6e, commit 5a95d73f

<a id="read_obtain_memo"></a>
#### `READ_OBTAIN_MEMO` — Descartado, def. 0

Razonamiento: Un muestreo de pilas situó el binding de buffers (~6,3 ms/fotograma) como mayor coste del render y se probó memorizar los obtains de lectura repetidos mientras no cambien los epochs. Es exacto (0 discrepancias en ~500 k aciertos) pero el render obtiene ~10 k rangos por fotograma a 0,26 µs y el epoch CPU-dirty vacía el memo ~750 veces por fotograma, así que el diccionario cuesta más de lo que ahorra. *[exacto-neutro]*

Fuentes: 3d7ca311 2026-10-17, commit ae777c83

<a id="skip_dcc_sync"></a>
#### `SKIP_DCC_SYNC` — Descartado, def. off

Razonamiento: ~2,5 s de cada 10 s del render eran SynchronizeGuestDccMetadata (~10 por fotograma, ~3,3 ms cada una) bajando a CPU el DCC de destinos de color recién escritos por la GPU; se probó saltársela. Empeora (6,52 FPS, 56 ms de espera) porque la espera solo se desplaza a otro punto de sincronía, que es donde el render alcanza a la GPU; hace falta bajar a la vez la CPU de render y el trabajo de GPU. Esas esperas bajaron después (36 → 16 ms) rellenando los registros 0x83D3 por record fills. *[adelanta-cuello]*

Fuentes: 3d7ca311 2026-10-10

<a id="speculative_table_reads"></a>
#### `SPECULATIVE_TABLE_READS` — Descartado, def. 0

Razonamiento: Leer la copia de CPU de las palabras de tabla que solo están sucias por bindings ≥16 MB, bajo la hipótesis de que son falsos positivos de seguimiento. Encontró 0 palabras así porque los bindings pequeños del refit también las cubren cada fotograma, y su verify esperaba de forma síncrona; el modo =3 sirvió como sonda de origen de las esperas. Más tarde STABLE_TABLE_READS se justificó explícitamente como distinto de este intento (palabras elegidas por historial, no por tamaño de binding). *[otro]*

Fuentes: 3d7ca311 2026-10-12, 3d7ca311 2026-10-15

<a id="stable_table_reads"></a>
#### `STABLE_TABLE_READS` — Descartado, def. 0

Razonamiento: El censo del 13-10 mostraba que el 72–73 % de las esperas síncronas de tabla eran sobre palabras que nunca cambian; el interruptor aprende por historial cuáles y lee la copia de CPU, comprobándolo después con una copia de 4 B en la cola de readback. El lote 2 se colgó en 10 de 14 sesiones (palabra rancia de blob+0x60 que 0xF6F2 escribe por un binding de 4 B); con la puerta de escritor pequeño no hubo cuelgues en 17 sesiones, pero tampoco ganancia porque solo mueve el vaciado de cola a las dependencias reales. *[adelanta-cuello]*

Fuentes: 3d7ca311 2026-10-15, 3d7ca311 2026-10-16, commit 02435d88, commit 8c9e973a

<a id="stable_table_reads_writer_max"></a>
#### `STABLE_TABLE_READS_WRITER_MAX` — Descartado, def. 0x40

Razonamiento: Todos los cuelgues del lote 2 siguieron a una lectura especulativa de una palabra de blob+0x60 que 0xF6F2 escribe por un binding de 4 B y que valía 0 durante el aprendizaje. La puerta marca como no especulables las palabras dentro de bindings de escritura ≤0x40 B (las dependencias reales tienen escritores exactos de una palabra: B198+0x18, 02F1+0x4, F6F2+0x4, 3F20+0x18), mientras las palabras sobre-marcadas que interesan están en bindings de 0x100 o más. *[estabilidad]*

Fuentes: 3d7ca311 2026-10-15, commit 8c9e973a

<a id="sync_indirect_args"></a>
#### `SYNC_INDIRECT_ARGS` — Descartado, def. off

Razonamiento: Hipótesis del giro de RT: los argumentos de un dispatch indirecto los habría escrito un dispatch grabado por delante de la GPU y se leerían rancios (grupos a 0), así que se sincronizó la GPU antes de leerlos. rt106int dio 0 dispatches indirectos con argumentos a 0 en toda la tirada, con o sin el interruptor: la hipótesis queda descartada y el giro no cambia. *[corrección]*

Fuentes: 3d7ca311 2026-10-09

<a id="sync_readback_queue"></a>
#### `SYNC_READBACK_QUEUE` — Descartado, def. off

Razonamiento: Segunda cola de la familia de readback para las lecturas síncronas del render, dentro del paquete de censo/ticks precisos del 12-10. Es exacta pero neutra en FPS (3+3); el 29-10, junto con READBACK_PRECISE_WAIT=1, una sola sesión estática bajó GpuCompletionWait 1,3 ms sin confirmar y en paneo fue neutra. *[exacto-neutro]*

Daniel: Según el resumen del 15-10, Daniel aceptó dejarla (exacta pero neutra) para re-probarla cuando el bucle del juego fuera más rápido.

Fuentes: 3d7ca311 2026-10-12, 3d7ca311 2026-10-15, 3d7ca311 2026-10-29, commit 6833c8b7

<a id="texel_clean_skip"></a>
#### `TEXEL_CLEAN_SKIP` — Descartado, def. 0

Razonamiento: Regla de upstream: una lectura texel sin páginas CPU-sucias salta la subida vacía. Entró como recorte por draw (c5813da2), pero el paneo perdió ~2 FPS con ella y quedó opt-in (280dd207); en la fusión del 03-11 se restauró a propósito que una lectura limpia ejecute su subida (upstream la salta). Las pruebas en caliente en mundo abierto (hb02 +0,47, hb05 +0,12) quedaron dentro del ruido de ±1 FPS. *[A/B-upstream]*

Fuentes: 3d7ca311 2026-10-25, 3d7ca311 2026-11-03, 3d7ca311 2026-11-07, commit 280dd207

<a id="uniform_image_source"></a>
#### `UNIFORM_IMAGE_SOURCE` — Descartado, def. off

Razonamiento: El depth de 6 MB que el juego limpia a 0xFF en piezas de 1 MB cada fotograma se marcaba sucio y se copiaba entero (~2,1 ms/fotograma en el render); se probó detectar en memoria guest que todos los bytes son iguales y rellenar el staging con vkCmdFillBuffer. ImageUpload baja 2,4 → 1,8 ms pero el FPS queda en ruido (12,74 vs 12,89, 3 por brazo), así que es opt-in; el perfil apuntaba a un coste mayor, ~2,7 ms de re-protección de páginas del barrido de device-address. *[exacto-neutro]*

Fuentes: 3d7ca311 2026-10-11, commit 53175bbc

<a id="upload_gpu_wait"></a>
#### `UPLOAD_GPU_WAIT` — Descartado, def. off

Razonamiento: La cola de unión de subidas costaba 2,2–2,8 ms al hilo de envío; se probó que la GPU espere un timeline señalado por el worker en vez de unirlas en CPU. Solo traslada la espera (la GPU espera las subidas 2,2–2,5 ms, 26,18 FPS), así que queda apagado; lo que sí se encendió fue WORKERS=2 con unión exacta por prefijo y RING_WAKE=64, que baja la cola a 2,9–3,6 ms por fotograma. *[adelanta-cuello]*

Fuentes: 3d7ca311 2026-10-31, 3d7ca311 2026-11-01, commit 35c88e4c, commit fa51b8c0

<a id="written_obtain_memo"></a>
#### `WRITTEN_OBTAIN_MEMO` — Descartado, def. 0

Razonamiento: Memo por tick de los obtains de bindings escritos repetidos en el mismo command buffer. Es exacto (1,18 M verificaciones) pero neutro: el tiempo ahorrado pasa a espera, porque el fotograma está limitado por latencia (1 ms recortado del render mueve ~0,45 ms). Se dejó para re-probar cuando el bucle del juego sea más rápido. *[adelanta-cuello]*

Daniel: Según el resumen del 15-10, Daniel aceptó dejarlo (exacto pero neutro) para re-probarlo cuando el bucle del juego fuera más rápido.

Fuentes: 3d7ca311 2026-10-14, 3d7ca311 2026-10-15, commit be320381

<a id="written_sync_cpu_dirty"></a>
#### `WRITTEN_SYNC_CPU_DIRTY` — Descartado, def. off

Razonamiento: Se buscaba explicar el patrón «gpu0/guest0» de 49 de 80 cabeceras BVH: la GPU escribe la cabecera pero la página sigue CPU-sucia y la siguiente sincronización de un binding de lectura subiría la página del huésped encima, dejando las cuentas a 0. rt115int con el interruptor dio 0,94 FPS y el mismo giro con last_node=−1; las cabeceras salen gpu_dirty=True cpu_dirty=False, así que una subida posterior no las borra. *[corrección]*

Fuentes: 3d7ca311 2026-10-09

<a id="readback_wait_ms"></a>
#### `READBACK_WAIT_MS` — Sonda, def. off

Razonamiento: Sonda de tiempo de pared en VulkanAsyncReadback.Wait. En la rama precisewait2 la estática daba ~23,8 FPS frente a 28,28 del control sin la sonda, y el coordinador la señaló como culpable probable de la caída: no usarla en A/B de FPS. *[coste-sonda]*

Fuentes: 3d7ca311 2026-10-23, commit e782a1df

### descriptores/materialización (59)

<a id="bda_sweep_per_submit"></a>
#### `BDA_SWEEP_PER_SUBMIT` — ON, def. on

Razonamiento: Un muestreo de CPU del hilo de render mostró que PrepareBda corría en cada dispatch (~12 por fotograma), subiendo y reprotegiendo las mismas páginas calientes (~11 % del hilo). Basta un barrido por envío, pues lo que lee un envío es lo que la CPU escribió antes de enviarlo: +8 % de FPS. *[ganancia]*

Fuentes: 3d7ca311 2026-10-10

<a id="da_zero_page"></a>
#### `DA_ZERO_PAGE` — ON, def. 1

Razonamiento: Las cargas por dirección de dispositivo llevaban ramas (en tabla / en carga); la hipótesis era que leer siempre de una página de ceros sin ramas ahorraría hasta ~3 ms de dispatch. El agente lo entregó apagado (−1,1 ms estática, −1,9 paneo, 0 páginas perdidas, lejos de la cota) y el coordinador lo encendió por el criterio de ms aunque no diera FPS visibles. El hueco hasta la cota se atribuyó, sin medirlo, al acumulador de fallos de un shader de RT que ya va al tope de registros. *[ganancia]*

Fuentes: 3d7ca311 2026-10-17, commit 678695fa

<a id="dcc_clear_lean"></a>
#### `DCC_CLEAR_LEAN` — ON, def. on

Razonamiento: Medido con contadores: de ~5,4 k llamadas por fotograma, ~940 llevan DCC y solo ~20 tienen clave de clear, pero se alquilaba el búfer y se corría el bucle en todas. Se leen primero las claves y solo se corre el bucle si alguna es clear: mismo resultado por construcción, parte del lote barato de 11 commits (+0,4 FPS en conjunto, sin separar su ms). *[sin-A/B]*

Fuentes: 3d7ca311 2026-10-12

<a id="dcc_scan_in_place"></a>
#### `DCC_SCAN_IN_PLACE` — ON, def. on

Razonamiento: MaterializeDccClear costaba 1,8 ms de los ~4,3 ms de ImageLookup por copiar la clave y la loncha entera en trozos de 64 KB comparando byte a byte. Se vectorizó (IndexOfAnyExcept, 1,29 ms) y se leyó en sitio por el alias del backing (0,99 ms); resultado idéntico por construcción y captura correcta. *[ganancia]*

Fuentes: 3d7ca311 2026-10-11

<a id="depth_color_copy"></a>
#### `DEPTH_COLOR_COPY` — ON, def. on

Razonamiento: Las sombras de GTA V alternan D16 y R16; con VK_KHR_maintenance8 se copia con un solo vkCmdCopyImage en vez de ir y volver por búfer. Se confirmó que el camino está caliente (~4 copias/fotograma) y que en CPU es neutro (ImageOverlap 0,30 vs 0,28 ms); iba en el lote barato, sin ms propio medido. *[sin-A/B]*

Fuentes: 3d7ca311 2026-10-12

<a id="device_store_tables"></a>
#### `DEVICE_STORE_TABLES` — ON, def. on

Razonamiento: Los BLAS degenerados (~150 → 1) venían de stores perdidos por la tabla de páginas; el planner registra como rangos escritos los V# de las tablas de store (confirmado en rt222–227). Pero tumbaba el juego (AV de lectura de huésped a ~105 s), se apagó, y vuelve por defecto con filtro de dirección + fusión. Hipótesis sin verificar del AV: marcar ~17.700 V# como escritos protege páginas de CPU que el huésped lee. Al fusionar con upstream, su vía rápida se excluye si el interruptor está activo para no perder estos rangos. *[corrección]*

Fuentes: 3d7ca311 2026-10-09, 3d7ca311 2026-11-03

<a id="device_store_tables_maxaddr"></a>
#### `DEVICE_STORE_TABLES_MAXADDR` — ON, def. 0x1000000000

Razonamiento: Registrar los ~17.700 V# de la tabla como rangos escritos tumbaba el juego porque incluía memoria de CPU que el huésped lee; el umbral de dirección deja fuera esa memoria (rt246filt: sin AV en 260 s). Pero solo, daba 2,25 FPS: registrar miles de rangos en cada dispatch costaba BufferAcquisitionChecks 230 ms/fotograma (109.347 adquisiciones); la fusión posterior es lo que lo hace utilizable. *[corrección]*

Fuentes: 3d7ca311 2026-10-09

<a id="device_store_tables_merge"></a>
#### `DEVICE_STORE_TABLES_MERGE` — ON, def. 0x10000

Razonamiento: Tras el umbral de dirección, había que reducir la cantidad de rangos: fusionar huecos ≤ 64 KB baja la tabla de 6A53 de ~17.700 rangos a 34 y deja 6,26 FPS sin AV. Efecto lateral entendido después (17-10): el rango escrito resultante de ~40 MB lo marcan 10CD y 6A53 y no se puede acotar estáticamente porque eligen el V# por un waterfall con índice por lane. *[corrección]*

Fuentes: 3d7ca311 2026-10-09, 3d7ca311 2026-10-17

<a id="device_store_tables_written"></a>
#### `DEVICE_STORE_TABLES_WRITTEN` — ON, def. on

Razonamiento: Hipótesis: para arreglar los stores perdidos bastaba registrar los V# de la tabla, sin marcarlos como escritos (el marcado sucia páginas de 4 KB enteras y los hilos del juego esperan ~16 ms por lectura de etiqueta). Con =0 rompe (6,46 y 3,63 FPS, picos de recorrido 216–266 ms: las subidas de CPU pisan lo escrito por GPU), así que se mantiene encendido y el problema de las etiquetas se ataca con precisión de byte y la superposición de escrituras del frente. *[corrección]*

Fuentes: 3d7ca311 2026-10-10

<a id="indirect_image_dim"></a>
#### `INDIRECT_IMAGE_DIM` — ON, def. 1

Razonamiento: D44E (pase que sombrea los aciertos de reflejos) se saltaba desde ~118 s, al empezar el paneo, porque el juego mete en su tabla de materiales dos rellenos 1×1 (uno 3D y otro 2D entero) y el materializador rechazaba la mezcla de dimensiones/tipos. Con =1 D44E corre en el 100 % de los fotogramas y desaparecen las manchas magenta; el coordinador lo puso por defecto y hubo que ajustar 11 tests cuyo fixture «incompatible» era justo esa mezcla. *[corrección]*

Fuentes: 3d7ca311 2026-10-30

<a id="indirect_image_msaa"></a>
#### `INDIRECT_IMAGE_MSAA` — ON, def. 1

Razonamiento: El magenta de paredes, puertas y cristales: el pase 8395 deja un marcador rosa «pendiente de sombrear» por cada acierto y D44E lo sobrescribe; si D44E se salta, el rosa se queda. El juego mete a veces un relleno de profundidad multimuestra 1×1 en la tabla y el emulador rechazaba el pase entero el resto de la sesión (un tercio de las veces en st09). Un T# multimuestra es inalcanzable desde un image_sample 2D, así que se anula; se dejó encendido y =0 recupera ~0,7 FPS a cambio de que vuelva el rosa. *[corrección]*

Fuentes: 3d7ca311 2026-11-05

<a id="indirect_image_stride0"></a>
#### `INDIRECT_IMAGE_STRIDE0` — ON, def. 1

Razonamiento: D44E seguía saltándose fuera del banco del prólogo (st33: «material stride=0 selector stride=188»): la tabla de materiales llegaba en un V# de stride 0 y las cargas escalares ignoran el stride y direccionan por bytes. Se lee por bytes igual que ReadScalarBufferWord; en st34 no hubo avisos. *[corrección]*

Fuentes: 3d7ca311 2026-11-06

<a id="indirect_limits_in_shader_data"></a>
#### `INDIRECT_LIMITS_IN_SHADER_DATA` — ON, def. on

Razonamiento: Los dispatches indirectos por hilos no podían tener sus límites escritos por la GPU, así que el hilo de render leía los contadores y volvía a preparar el programa en cada dispatch (una materialización completa cada vez); la cadena BVH de GTA V (diez programas 64x1x1, ~80 dispatches/frame) lo sufría. Ahora llevan los límites en ShaderData y la GPU copia los contadores; la alternativa de preparar los indirectos en el frente (FRONT_PREP_INDIRECT) no daba casi nada. *[ganancia]*

Fuentes: 3d7ca311 2026-10-11

<a id="lean_texture_path"></a>
#### `LEAN_TEXTURE_PATH` — ON, def. on

Razonamiento: PrepareBindings corre ~3000 veces por fotograma, así que se añadió un camino delgado sin copias de la petición para el caso por defecto, detrás de un interruptor y dejando intacto el memo (apagado y más complejo). Era el paso S1 del plan para adelgazar el camino de texturas; el A/B en juego quedó pendiente y tras el merge de #1082 ya no cambia nada. *[sin-A/B]*

Fuentes: 3d7ca311 2026-10-11

<a id="materialization_cache"></a>
#### `MATERIALIZATION_CACHE` — ON, def. on

Razonamiento: Se sospechó que la caché de materialización por contenido servía un acierto rancio (el puntero s[4:5] no cambia aunque la CPU reescriba el bloque para otro BLAS) y por eso los datos de BLAS llegaban a cero. Se añadió el interruptor =0 para A/B; con la caché apagada (rt120int) los 84 hdrpost seguían con pairs=0, así que no era la causa. *[otro]*

Fuentes: 3d7ca311 2026-10-09

<a id="materialization_content_key"></a>
#### `MATERIALIZATION_CONTENT_KEY` — ON, def. on

Razonamiento: La caché de materialización acertaba poco (21 %) porque su clave incluía punteros que cambian por anillo de fotograma. Un análisis de uso del user data (valor / solo dirección / dword de buffer puro) hace que la clave use solo los registros de valor y los rangos se rebasan por el delta del puntero, validando contra las palabras que leería ese draw; MaxVariants 4 → 8. Dio 12,2 vs 10,94 FPS con render idéntico. *[ganancia]*

Fuentes: 3d7ca311 2026-10-07, 3d7ca311 2026-10-08

<a id="materialize_rebase"></a>
#### `MATERIALIZE_REBASE` — ON, def. 1

Razonamiento: Con el registro de rancias (STALE_LOG) se vio que la tabla de descriptores y las direcciones de buffer dentro de ella se mueven el mismo delta por los anillos de fotograma (+0xA0000 en las muestras), y un único plan (08C00FC1…) explicaba el 43–59 % de las entradas rancias. Rebasar esas entradas por el delta evita re-materializarlas. En la fusión con upstream (11-2026) se mantuvo el interruptor y el valor 1 porque la caché de fusión es la nuestra. *[ganancia]*

Fuentes: 3d7ca311 2026-10-12, 3d7ca311 2026-11-04

<a id="precise_table_words"></a>
#### `PRECISE_TABLE_WORDS` — ON, def. all

Razonamiento: Las lecturas síncronas de tabla de la cadena del TLAS (F5EB, 4481, 994A) vaciaban el lote abierto; primero se probó la lectura precisa (escritor exacto) con lista <hash>+<offset>, que daba 100 % de fallbacks (77 % con el escritor sin enviar, 23 % en vuelo) aunque el verify diera 0 discrepancias, sin ahorrar nada. El paquete genérico G (DEVICE_DATA_READS=auto, PRECISE_TABLE_READ=1 con WORDS=all, CP_WRITE_CLEAN=1) es exacto, sin hashes, y ganó +0,30 FPS en paneo y +0,72 en estática, así que se activó por defecto en lugar del paquete K. *[ganancia]*

Fuentes: 3d7ca311 2026-10-31, 3d7ca311 2026-11-01

<a id="program_gate_tables"></a>
#### `PROGRAM_GATE_TABLES` — ON, def. 3

Razonamiento: Al quitar el cerrojo de los aciertos de programa (ProgramGate 6,4 → 0 ms/frame) su autor halló un fallo preexistente: en paneo la caché compartida devolvía rangos viejos de las tablas de store, porque un scan de tabla sacado de la caché de scans no deja grabadas sus palabras (2.807 de 10,24 M aciertos discrepaban; ~20.000 aciertos/sesión que una materialización nueva habría pasado al render). La causa concreta era una tabla de 2 MB y 131.072 entradas (compute 10CD/6A53). El modo 3 reevalúa, es exacto y sale más barato que el modo 0, así que se dejó por defecto. *[corrección]*

Fuentes: 3d7ca311 2026-10-24, 3d7ca311 2026-10-25

<a id="program_gate_table_stamp"></a>
#### `PROGRAM_GATE_TABLE_STAMP` — ON, def. no se lee (siempre activo)

Razonamiento: Con ProgramGate sin cerrojo, las cachés por hilo rechazan el acierto de un plan con tablas de store si hubo una invalidación de tabla desde su recorrido, porque los escaneos cacheados no son lecturas grabadas. Es una pieza de corrección de ese cambio, sin cifra propia. *[corrección]*

Fuentes: 3d7ca311 2026-10-24

<a id="recorder_blocks"></a>
#### `RECORDER_BLOCKS` — ON, def. 1

Razonamiento: En paneo el frente gastaba ~8 ms/frame copiando palabra a palabra las lecturas residentes grabadas (BigBuild/BigWalk). Grabar como bloque toda lectura residente ≥4 KB en un fallo y copiarla de una vez los quitó, dejando el frente casi como en estático (front busy −8,0 ms/frame), y se encendió por defecto. La alternativa de decodificación incremental no se hizo porque ahorraría como mucho ~1 ms/frame. *[ganancia]*

Fuentes: 3d7ca311 2026-10-16

<a id="recorder_runsort"></a>
#### `RECORDER_RUNSORT` — ON, def. 1

Razonamiento: La sonda MATERIALIZE_SPLIT mostró que los +7,4 ms de Materialize_miss que quedaban en paneo eran fallos grandes que recorren la tabla de 2 MB: BigBuild +4,7 (de ellos BigSort +3,5) y BigWalk +2,3. Como las lecturas grabadas llevan pocos tramos ascendentes largos, se fusionan esos tramos en vez de un introsort completo: front BigSort −1,6 ms y CPU del frente −2,0 en paneo, sin efecto en estática, y por defecto. *[ganancia]*

Fuentes: 3d7ca311 2026-10-16

<a id="render_pools"></a>
#### `RENDER_POOLS` — ON, def. on

Razonamiento: La CPU del hilo de render (46–50 ms) incluía mucha asignación y GC por draw, así que se pasaron las preparaciones, etapas y bindings a pools; la asignación bajó de 4450 a 569 KB/frame. Se contrastó con una alternativa solo de entorno (DOTNET_GCgen0size a 256 MB) en un brazo aparte, y al revisar descriptores el 17-10 se dio por hecho que las asignaciones en caliente ya estaban recortadas por este interruptor. *[ganancia]*

Fuentes: 3d7ca311 2026-10-16, 3d7ca311 2026-10-17

<a id="resource_cache"></a>
#### `RESOURCE_CACHE` — ON, def. on

Razonamiento: Es la caché de reutilización de materializaciones que viene de upstream (=0 materializa en cada draw). En las sesiones perf5 de 2026-10-07 se vigilaban sus aciertos/fallos/rancias (una ventana: 124.953 aciertos, 45.211 fallos, 44.088 rancias), y sobre ella se construyeron después CONTENT_KEY, REBASE y SYNC_VALIDATE; el interruptor sirve sobre todo para A/B y diagnóstico. *[sin-A/B]*

Fuentes: 3d7ca311 2026-10-07

<a id="stage_memo"></a>
#### `STAGE_MEMO` — ON, def. on

Razonamiento: Tras fusionar origin/main faltaban ~4 ms de CPU de render; se añadió el interruptor para hacer A/B del memo de etapas de upstream (defecto upstream=1). Hallazgo: 3368666f no tenía ese memo y en GTA la reutilización de upstream cuesta 6,4 ms (FINE) frente a 4,6 de la resolución antigua, porque en el 76 % de los aciertos cambian los descriptores de búfer y FindBuffers se repite; se mantiene encendido y se complementó con BUFFER_SOURCE_MEMO. *[A/B-upstream]*

Daniel: Principio de Daniel: si una optimización es exacta va sin interruptor; si cambia comportamiento de upstream lleva interruptor con defecto lo nuestro y lo de upstream opt-in.

Fuentes: 3d7ca311 2026-11-04, 3d7ca311 2026-11-05, commit 3a5ff60d

<a id="stage_memo_touch"></a>
#### `STAGE_MEMO_TOUCH` — ON, def. on

Razonamiento: IsFreshStagePreparation tomaba dos veces el cerrojo de la caché de imágenes por textura recordada; se fusionó en una sola toma con las mismas comprobaciones (exacto por construcción). Estimación por RIP ~0,3 ms, medido ≈0; se deja encendido por ser exacto y barato. *[exacto-neutro]*

Fuentes: 3d7ca311 2026-11-05, commit adfdd6e7

<a id="store_table_applied_check"></a>
#### `STORE_TABLE_APPLIED_CHECK` — ON, def. on

Razonamiento: Carrera de la caché de tablas: la generación por caché solo se movía si ya había una entrada solapada, así que una escritura (fallo de CPU, CP) durante el primer escaneo se perdía y quedaban rangos previos a la escritura. Se añadió una generación global y este check descarta preparaciones adelantadas (front/worker) si se aplicó una escritura de tabla entre su inicio y su ejecución; coste despreciable, por eso en on. *[corrección]*

Fuentes: 3d7ca311 2026-10-23, commit 888f79e4

<a id="store_table_bulk"></a>
#### `STORE_TABLE_BULK` — ON, def. 1

Razonamiento: En paneo la causa no era la GPU sino el hilo del frente: una tabla de 2 MB (131.072 entradas) se releía entera palabra a palabra cada vez que el juego escribía 8 B (~20 veces cada 5 s, ~15 ms por frame). Hipótesis: leerla en bloques de 64 KB con el lector residente; nació apagado, =2 comparó 84 M palabras sin diferencias reales y se encendió tras la prueba en estática. *[ganancia]*

Fuentes: 3d7ca311 2026-10-16

<a id="store_table_diff"></a>
#### `STORE_TABLE_DIFF` — ON, def. 1

Razonamiento: La CPU escribe 8 bytes por fotograma (siempre los mismos) en la tabla de 2 MB y cada drop la reescaneaba entera: 1,9 ms por frame en el front con GATE_TABLES=3. Ahora se relee y compara por trozos con el último escaneo y, si no cambia, se reutiliza la lista (decodifica solo desde el trozo que difiere). Exacto (DIFF=2: 0/4.066), con +0,33 ms de coste pero desaparece el BigWalk, así que quedó por defecto junto a GATE_TABLES=3. *[ganancia]*

Fuentes: 3d7ca311 2026-10-24, 3d7ca311 2026-10-25, commit 8741f3de

<a id="table_word_memo"></a>
#### `TABLE_WORD_MEMO` — ON, def. 1

Razonamiento: F43B esperaba 4,4 ms leyendo la cabecera de los BLAS animados, que nunca cambia (changed=0); la espera existía porque varios shaders (refit 5981, 8395/9FBC, 6A53/10CD) marcan el blob entero como escrito. Un memo exacto con lista blanca de escritores la quita (0 discrepancias) pero la espera total solo baja ~0,4 ms porque se muda a B198, cuya dependencia sí es real; se activó (con TLAS_HLE) tras verificar 0/1223 reutilizaciones en paneo. *[adelanta-cuello]*

Fuentes: 3d7ca311 2026-10-22, commit 096bb247, commit 7c7b0178

<a id="table_word_memo_cp_update"></a>
#### `TABLE_WORD_MEMO_CP_UPDATE` — ON, def. on

Razonamiento: Las palabras de F43B, F6F2 y 994A que se sincronizan cada frame las reescribe el procesador de comandos con el mismo valor; el memo las tiraba en cada escritura del CP y la siguiente lectura esperaba al lote BVH en vuelo. Aplicar el dato del CP en orden de flujo es exacto (=2: 0 discrepancias); el 24-10 la espera se mudó a B198 y no se activó, el 30-10 se encendió por la bajada de GpuCompletionWait en paneo. *[adelanta-cuello]*

Fuentes: 3d7ca311 2026-10-24, commit 3b1db289

<a id="table_word_memo_learn"></a>
#### `TABLE_WORD_MEMO_LEARN` — ON, def. 8

Razonamiento: Lista de escritores aprendida por (palabra, programa) para quitar la lectura síncrona de 994A (censo 2,95→0,70 ms, 0 discrepancias en 2,2 M de auditorías). Sola perdía FPS en paneo (28,78→28,24): esa lectura también cortaba el lote abierto y enviaba pronto a la GPU el refit de 5981 y lo siguiente, y sin ese corte el trabajo que leen los hilos del juego llega tarde y el front espera en REWIND. Con PRECISE_WRITE_TICKS=1 y AUDIT=64 pasó a 8 por defecto. *[adelanta-cuello]*

Fuentes: 3d7ca311 2026-11-01, 3d7ca311 2026-11-02

<a id="unreadable_image_table_reject"></a>
#### `UNREADABLE_IMAGE_TABLE_REJECT` — ON, def. on

Razonamiento: D44E falló fatal (reason=Other) justo al subir al coche fuera del banco del prólogo; antes del arreglo del rosa D44E se saltaba y nunca llegaba ahí. La causa exacta no está confirmada (retornos silenciosos del materializador al leer tablas de imágenes indirectas); se rechaza el dispatch como IncompatibleImageCandidates y se reintenta, y el fatal ahora lleva el detalle. Quedó pendiente de validar jugando. *[estabilidad]*

Fuentes: 3d7ca311 2026-11-06, commit 252f4cc4

<a id="validate_in_place"></a>
#### `VALIDATE_IN_PLACE` — ON, def. 1

Razonamiento: El desglose del render en paneo mostró que el shader 6A53 con su tabla de store de 2 MB cae en el hilo de render, que validaba ~9–11 MB/frame copiándolos (Validate 1,1–1,6 ms) y reservaba un array de 2 MB por llamada. Se comparan en sitio con las mismas comprobaciones de propiedad y se saltan tramos iguales vectorizados. Se aceptó por mecanismo (~0,45 ms/frame), no por FPS, porque el A/B es ruidoso. *[ganancia]*

**Contradicción:** El 25,66→27,13 de estática del registro incluye un 21,72 de modo lento en el brazo apagado; se aceptó por mecanismo, no por FPS.

Fuentes: 3d7ca311 2026-10-24, commit 2c0d8dba

<a id="device_store_handles"></a>
#### `DEVICE_STORE_HANDLES` — OFF, def. off

Razonamiento: Generalizar los rangos escritos exactos a programas cuyos V# de store salen de palabras que se cargan, si se pueden evaluar sin esperar. Apenas aplica: 260F planifica 0 veces (sus cuentas las escribe la GPU justo antes) y 994A 4 frente a 3.254 bloqueantes; 994A queda en el dispositivo sin romper, pero la espera pasa a 11B4. *[adelanta-cuello]*

Fuentes: 3d7ca311 2026-10-22, commit 11791640

<a id="materialize_read_memo"></a>
#### `MATERIALIZE_READ_MEMO` — OFF, def. off

Razonamiento: Idea: una palabra releída dentro del mismo recorrido de descriptores devuelve el valor de la primera lectura (se sigue registrando cada lectura). En los menús donde corrió encontró 0 repeticiones y la única corrida (mst1a) se paró antes de salir del menú, así que si hay repeticiones en juego sigue sin comprobar. *[sin-A/B]*

Fuentes: 3d7ca311 2026-10-12

<a id="store_table_chunks"></a>
#### `STORE_TABLE_CHUNKS` — OFF, def. off

Razonamiento: En agc-hle: una escritura de CPU entre dos tablas lejanas toma el cerrojo AllCaches y recorre todas las entradas; se creó un rechazo por trozos de 64 KiB que alguna vez tuvieron tabla, sin saber si GTA lo sufría. Solo se midió combinado (brazo H con FAST_IMPORT_GATEWAY y los interruptores de memoria): estática 20,11 vs 20,90, sin ganancia, y nunca aislado; queda apagado. *[sin-A/B]*

**Contradicción:** El registro dice «sin medir»; sí se midió, pero solo combinado con FAST_IMPORT_GATEWAY y los interruptores de memoria (brazo H, peor que la base en estática).

Fuentes: 3d7ca311 2026-10-14, 3d7ca311 2026-10-15, commit 91660ecb

<a id="buffer_source_memo"></a>
#### `BUFFER_SOURCE_MEMO` — Descartado, def. 0

Razonamiento: La premisa (FindBuffers se repite sin necesidad) era falsa: buffer_refinds=0 y los descriptores de búfer cambian en el 76-77 % de los reusos. Aun así se probó una tabla (programa, slot, dirección, footprint), exacta pero que empeoró BindingMemoBuffers (2,09 a 2,66 ms) y el FPS (hb02 -1,05); se había puesto =1 en 3a5ff60d y se devolvió a 0. *[exacto-neutro]*

**Contradicción:** El commit 3a5ff60d lo dejó a 1 por defecto; el 06-11 se vio que empeora y se dejó apagado (el comentario del código sigue diciendo «1 by default»).

Fuentes: 3d7ca311 2026-11-04, 3d7ca311 2026-11-05, 3d7ca311 2026-11-06, commit 3a5ff60d

<a id="content_uploads"></a>
#### `CONTENT_UPLOADS` — Descartado, def. off

Razonamiento: El UploadDwords de upstream mete los datos de shader y las tablas en una arena por contenido (XxHash3 y búsqueda en diccionario por subida), lo que costaba 0,93 ms por fotograma en GTA V frente a 0,28 del anillo transitorio de 3368666f. Se dejó apagado por el criterio de preservar lo nuestro medido. *[A/B-upstream]*

Fuentes: 3d7ca311 2026-11-04, commit 91011b85

<a id="da_host_backing"></a>
#### `DA_HOST_BACKING` — Descartado, def. 0

Razonamiento: Se quería que los programas de la cadena BVH corrieran en el dispositivo leyendo el dato real del alias del huésped (memoria de host importada) en vez de caer a lecturas síncronas de CPU. Con modo 2 + barrido los programas F43B, B198, 5981, F5EB... corrían sanos, pero 260F seguía rompiendo el BVH y GpuCompletionWait no bajaba en ningún eslabón sano: la espera solo pasaba al siguiente lector (F5EB, 994A, 11B4). Se paró la cadena y se aparcó. *[adelanta-cuello]*

Fuentes: 3d7ca311 2026-10-18

<a id="da_host_backing_stores"></a>
#### `DA_HOST_BACKING_STORES` — Descartado, def. on (con HOST_BACKING=2)

Razonamiento: Los stores que entran por el alias no se anotaban como escritos por la GPU, así que los lectores de CPU veían datos rancios. El bitmap marca esas páginas y los lectores esperan al lote más nuevo con direcciones de dispositivo (conservador). Aun así 260F siguió rompiendo el BVH: ni los stores sin seguir ni las barreras lo explicaban, y quedó bloqueada la cadena en 260F y 994A. *[corrección]*

Fuentes: 3d7ca311 2026-10-18

<a id="da_zp_accum"></a>
#### `DA_ZP_ACCUM` — Descartado, def. 0

Razonamiento: Variantes de cómo la búsqueda de página de ceros anota las páginas sin mapear, para cerrar el hueco hasta la cota de −3 ms. Ninguna variante exacta lo cierra (el acumulador cuesta ~0,3–0,4 ms en D44E; la 1 es solo una cota de medición y la 2/3 sobrerreportan), así que se recomendó dejar la 0 y no activar nada. *[otro]*

Fuentes: 3d7ca311 2026-10-17

<a id="da_zp_tagged"></a>
#### `DA_ZP_TAGGED` — Descartado, def. off

Razonamiento: Variante para leer una sola entrada de tabla (dirección de la página de ceros con bit 0 y enmascarar) y ahorrar la segunda lectura. Junto a ZP_ACCUM no cierra el hueco hasta la cota de −3 ms y se dejó fuera de la integración. *[otro]*

Fuentes: 3d7ca311 2026-10-17

<a id="device_descriptor_reads"></a>
#### `DEVICE_DESCRIPTOR_READS` — Descartado, def. vacío

Razonamiento: El render esperaba ~5–6 ms/frame en vaciados por lecturas síncronas de la cadena del TLAS (F5EB, 4481, 994A), que son V# de escritura cubiertos por los rangos escritos conservadores de 10CD/6A53/8395/5515. Era la pieza sin verify del paquete K con hashes, que salió neutro en A/B (paneo −0,06, estática +0,2); se eligió el paquete genérico G (CP_WRITE_CLEAN, exacto) y no se generalizó esta lista. *[exacto-neutro]*

Fuentes: 3d7ca311 2026-10-31, 3d7ca311 2026-11-01

<a id="device_store_tables_filter"></a>
#### `DEVICE_STORE_TABLES_FILTER` — Descartado, def. off

Razonamiento: Para evitar el AV de DEVICE_STORE_TABLES se probó filtrar los rangos a mapeos con ORBIS_PROT_GPU_WRITE (un conjunto nuevo alimentado por los mapeos de memoria directa/flexible). Quita el AV pero deja 541 rangos y 5,48 FPS; fusionando con huecos de 1 MB baja a 4,97 FPS con QueueRelay 34,6 ms, porque los rangos grandes marcados como escritos cubren páginas que escribe la CPU. Gana el umbral de dirección con huecos de 64 KB. *[corrección]*

Fuentes: 3d7ca311 2026-10-10

<a id="device_store_tables_written_min"></a>
#### `DEVICE_STORE_TABLES_WRITTEN_MIN` — Descartado, def. 0

Razonamiento: Tras fallar WRITTEN=0, la hipótesis era que lo que lo rompía eran los blobs BLAS grandes, así que se marcaban como escritos solo los rangos ≥ N bytes. Sin ganancia (rt379wm, ~9,9 FPS igual): las esperas en etiquetas no limitaban. En 2026-10-22 =0x80 se usó como diagnóstico y tampoco quitó la espera de la etiqueta 0x59C900000: la página la ensucian otros rangos. *[exacto-neutro]*

Fuentes: 3d7ca311 2026-10-10, 3d7ca311 2026-10-22

<a id="device_table_hashes"></a>
#### `DEVICE_TABLE_HASHES` — Descartado, def. vacío

Razonamiento: Se trataban como descriptores de dispositivo los V# de ciertos shaders (B198, 02F1, F43B, 4481, F5EB) para sacar lecturas de la CPU. Con F5EB/4481 los BLAS salían rotos (1–2 FPS); con B198/02F1 funcionaba pero solo trasladaba la espera al siguiente lector del lote en vuelo (F5EB, F43B, 4481). La lección fue que quitar una lectura síncrona solo mueve el vaciado a la siguiente, y hay que vaciar de lecturas la cadena entera del frame. *[adelanta-cuello]*

Fuentes: 3d7ca311 2026-10-18, 3d7ca311 2026-10-31

<a id="image_binding_memo"></a>
#### `IMAGE_BINDING_MEMO` — Descartado, def. off

Razonamiento: PrepareBindings corre ~3000 veces por fotograma y se probó un memo de bindings de imagen (adaptado de #1077). Redujo las búsquedas (5.500 → 4.830) pero DescriptorPreparation subió +0,6 ms, sin ganancia neta. Medido después: solo el 18 % de las entradas son idénticas dentro del frame y el 1,1 % al anterior, y la salida depende de un estado de cachés que cambia entre draws, así que un acierto no permite saltarse el trabajo. *[otro]*

Fuentes: 3d7ca311 2026-10-10, 3d7ca311 2026-10-17

<a id="image_binding_memo_ways"></a>
#### `IMAGE_BINDING_MEMO_WAYS` — Descartado, def. 1

Razonamiento: Más vías por ranura en el memo de búsqueda de imagen para bajar los fallos por colisión. Con generación por rango + 8 vías FindImage bajó de 5.500 a 3.950 llamadas pero ImageLookup solo 4,2 → 3,7 ms y DescriptorPreparation +0,4: 12,67/12,56 frente a 12,65 FPS. Se cortó el A/B a 3 de 6 corridas porque el efecto era menor de 1 ms. *[exacto-neutro]*

Fuentes: 3d7ca311 2026-10-11

<a id="image_lookup_full_hash"></a>
#### `IMAGE_LOOKUP_FULL_HASH` — Descartado, def. off

Razonamiento: El 91 % de los fallos de la caché de búsqueda de imagen eran colisiones de ranura porque el hash solo cubría un subconjunto de campos de la petición; por eso agrandar la tabla no cambiaba nada. Con hash completo + 4.096 ranuras + generación por rango los aciertos suben (67 → 83 %, 88 % el 22-10) pero el coste de ImageLookup no baja (~1,45 ms/frame en todos los brazos): el coste es el camino, no los fallos. Se deja opt-in. *[exacto-neutro]*

Fuentes: 3d7ca311 2026-10-11, 3d7ca311 2026-10-22

<a id="image_lookup_slots"></a>
#### `IMAGE_LOOKUP_SLOTS` — Descartado, def. 256

Razonamiento: Se probó agrandar la tabla del memo de FindImage (y luego la revisión diaria del 22-10 propuso medir los 4.096 de upstream). rt448 no cambió los ~100k fallos por ventana; el 22-10, con 77k búsquedas/s, LookUpImage costaba ~1,45 ms/frame en todos los brazos (256: 37 ms/s; 4096: 37,7; 4096 + generación + hash completo: 35), así que no aporta en GTA. *[A/B-upstream]*

**Contradicción:** Registro dice que los fallos «no son colisiones»; las transcripciones (11-10) encontraron que el 91 % de los fallos eran colisiones de ranura por el hash parcial, y el 22-10 con 256 ranuras se veían 141k colisiones frente a 25k con 4096.

Fuentes: 3d7ca311 2026-10-11, 3d7ca311 2026-10-22

<a id="limits_in_shader_data_hashes"></a>
#### `LIMITS_IN_SHADER_DATA_HASHES` — Descartado, def. vacío

Razonamiento: Primera versión dirigida del experimento de límites en ShaderData: solo los programas de una lista llevan sus datos al buffer ShaderData para que la GPU lea los contadores indirectos. Aunque quitaba las lecturas de CPU daba 8,9–9,6 FPS frente a ~10,1 (rt384) y quedó apagado; después se sustituyó por la regla genérica INDIRECT_LIMITS_IN_SHADER_DATA. *[otro]*

Fuentes: 3d7ca311 2026-10-10

<a id="materialize_poh"></a>
#### `MATERIALIZE_POH` — Descartado, def. off

Razonamiento: Los fotogramas perdidos en el paneo eran ráfagas limitadas por la CPU del render (CommandStream ~45 vs ~33 ms; +25 % draws, +31 % dispatches), y se probó mover los arrays de la caché de materialización al montón fijado con gen0 grande para reducir pausas de GC. Redujo las pausas a la mitad pero con coste neto (+0,46 ms render, +1,65 ms front), y se integró apagado. *[coste-sonda]*

Fuentes: 3d7ca311 2026-11-03

<a id="range_lookup_generation"></a>
#### `RANGE_LOOKUP_GENERATION` — Descartado, def. off

Razonamiento: Cada imagen creada o borrada (varias veces por frame) tiraba todas las búsquedas cacheadas; se invalidaba solo por las páginas de 1 MB que cubre cada búsqueda para ahorrar ~4 ms de ImageLookup. Sin ganancia medible (12,67/12,56 vs 12,65 FPS, efecto < 1 ms): se paró el A/B en 3 de 6 corridas y queda apagado. *[exacto-neutro]*

Fuentes: 3d7ca311 2026-10-11

<a id="store_table_compare"></a>
#### `STORE_TABLE_COMPARE` — Descartado, def. 0

Razonamiento: El perfil del render en fotogramas perdidos era plano; lo mayor y redundante que crecía era el reescaneo de la tabla de store (memcpy de trozos 0,34→1,07 ms y SequenceEqual 0,41→0,98 ms por intervalo). Se comparó en sitio en vez de copiar: exacto (0 discrepancias) pero no mueve la grabación ni los FPS, así que queda apagado. *[exacto-neutro]*

Fuentes: 3d7ca311 2026-11-03, commit 06408593

<a id="store_table_exact"></a>
#### `STORE_TABLE_EXACT` — Descartado, def. 0

Razonamiento: Para quitar la dependencia falsa del binding de 40 MB (4481 esperaba una palabra marcada escrita) se intentó marcar escritos solo los V# que evalúan los handles de store. Descartado: 10CD y 6A53 eligen el V# con un waterfall sobre un índice por lane leído de memoria, así que nunca se pudo acotar (todos los dispatches cayeron al barrido); además los 40 MB son un único V# y aun acotando V# no se excluiría la palabra. Con =2 en paneo 0 discrepancias pero sin exclusiones. *[otro]*

Fuentes: 3d7ca311 2026-10-17, commit d2533c64

<a id="store_table_fast"></a>
#### `STORE_TABLE_FAST` — Descartado, def. 0

Razonamiento: Parte del paquete render2 (con VALIDATE_IN_PLACE y MAPPED_SIZE_KEEP): releer en piezas de 4 KB un trozo rechazado por el lector residente. Se dejó apagado porque no movió nada (scan_ms 4,0 → 4,0 con la misma tasa de trozos rechazados), mientras los otros dos sí recortaron render. *[exacto-neutro]*

Fuentes: 3d7ca311 2026-10-24, commit 2c0d8dba

<a id="table_blocks"></a>
#### `TABLE_BLOCKS` — Descartado, def. off

Razonamiento: El table block de upstream (#1130) usa un layout de descriptores que el traductor de 3368666f no conoce y rompe la construcción dinámica de BLAS en GTA (todos los árboles guardados, 4,6 FPS). Se creó el interruptor con defecto upstream (a6ad6467, para no romper ShaderPrewarmPresenterTests) y Daniel decidió seguir con la fusión con el principio «defecto = lo nuestro, upstream opt-in», así que 09b013c9 lo puso a 0 con un override AsyncLocal para el test. *[A/B-upstream]*

Daniel: Decisión de Daniel (2026-11-04): seguimos con la fusión; donde upstream cambió comportamiento y lo nuestro funciona en GTA, interruptor con defecto lo nuestro y upstream opt-in.

Fuentes: 3d7ca311 2026-11-04, commit 09b013c9, commit a6ad6467

<a id="table_word_memo_learn_flush"></a>
#### `TABLE_WORD_MEMO_LEARN_FLUSH` — Descartado, def. off

Razonamiento: Hipótesis: la lectura síncrona de 994A además de esperar cortaba el lote abierto; mantener ese corte sin esperar (unos 8 cortes por fotograma) es exacto por construcción porque solo mueve un límite de lote. Recuperó la mitad de lo perdido (28,58 vs 28,20, base 28,94) y quedó innecesario cuando PRECISE_WRITE_TICKS=1 resolvió el corte. *[adelanta-cuello]*

Fuentes: 3d7ca311 2026-11-01

### pipelines/shaders (34)

<a id="async_compute_pipelines"></a>
#### `ASYNC_COMPUTE_PIPELINES` — ON, def. on

Razonamiento: Un compilador de host podía tardar decenas de segundos en un programa de cómputo grande y el flujo de comandos no avanzaba (la caché de pipelines retenía su cerrojo durante la creación). Upstream lo compila en un worker: la primera vez responde «no listo», con compilaciones en vuelo acotadas. *[otro]*

Fuentes: commit 629ebeaa

<a id="async_graphics_exempt"></a>
#### `ASYNC_GRAPHICS_EXEMPT` — ON, def. on (WAIT_MS 20)

Razonamiento: Con ASYNC_GRAPHICS_PIPELINES=2 el minimapa perdía el mapa: un draw saltado se pierde para siempre si nada redibuja su destino cada fotograma. Se exime (esperando hasta 20 ms a la compilación) a draws a targets lineales (los lee la CPU) o con todos los colores ≤1024x1024 (atlas, capas en caché); idea de TheCruZ/KytyPS5-GTA a6f9efcf reimplementada. *[corrección]*

Fuentes: 3d7ca311 2026-11-06, commit dea88aa5

<a id="da_zero_page_branch"></a>
#### `DA_ZERO_PAGE_BRANCH` — ON, def. on

Razonamiento: La contabilidad de fallos de la página de ceros se hacía con selects y stores en cada resolución de dirección; se metió en una rama sobre el fallo. Exacto por construcción (verify DA_ZERO_PAGE=2: lost=0) y se encendió porque 8395 baja de 6,86 a 6,29 ms y, con solo este interruptor frente a todo apagado, el paneo sube de 26,39 a 27,20 y la estática de 29,19 a 29,61 (6 sesiones por brazo). *[ganancia]*

Fuentes: 3d7ca311 2026-10-30, commit 9f5c7738

<a id="depth_bounds_key"></a>
#### `DEPTH_BOUNDS_KEY` — ON, def. 2

Razonamiento: En paneo se creaban ~155 pipelines por segundo durante toda la sesión: el 85 % tiene el test de depth bounds activo y los límites son lo único que varía en la clave; la idea de upstream #1078 (sacarlos solo con el test apagado) no habría servido. Modo 2 los pasa a estado dinámico: 0 creaciones, resolve −2,2 ms. Se puso por defecto el 13-oct por el mecanismo medido y +0,63 ± 0,42 FPS (1,5 errores típicos), reconociendo que una lectura estricta de la regla «gana en paneo» lo dejaba opt-in; queda pendiente una confirmación 2+2. *[ganancia]*

Fuentes: 3d7ca311 2026-10-13, commit 94a6d3f9, commit d8251317

<a id="fract_clamp"></a>
#### `FRACT_CLAMP` — ON, def. on

Razonamiento: x - floor(x) se redondea a exactamente 1,0 para entradas negativas diminutas, mientras el hardware devuelve el mayor valor menor que uno; se recorta con comparación ordenada y select (NaN pasa; f16 a 0x3BFF). Va encendido como corrección con test de dispositivo que falla con =0; no se sabe si el shader de sombras RT de GTA produce alguna vez esa fracción, y no hay cambio visible medido. *[corrección]*

Fuentes: 3d7ca311 2026-10-13, commit cccac37f

<a id="glc_atomic_loads"></a>
#### `GLC_ATOMIC_LOADS` — ON, def. on

Razonamiento: Los grupos del look-back (scan de un paso 0x220A) leían copias rancias de una fila de tickets: un grupo llevaba 5 M vueltas viendo 0 mientras otro ya veía la fila publicada. Las cargas GLC no son coherentes en el host NVIDIA; Volatile no basta, así que se emiten como OpAtomicLoad de ámbito dispositivo solo para las cargas GLC (más fino que COHERENT_BUFFERS) y quedó encendido, con test mínimo ComputeGlcSpinDeviceTests. *[corrección]*

Fuentes: 3d7ca311 2026-10-09

<a id="indirect_image_pad"></a>
#### `INDIRECT_IMAGE_PAD` — ON, def. 1

Razonamiento: Cada dispatch de 0xD44E traía su propia tabla de imágenes y el SPIR-V dependía de cuántos candidatos había por clase y de la profundidad de la búsqueda: 22 especializaciones por sesión, con compilaciones de ~3,2 s que dejaban al hilo de render hasta 13,2 s parado; una caché persistente no ayudaría con combinaciones nuevas porque la del driver ya persiste. Rellenar los tramos de clase a tamaño fijo es exacto y gratis en FPS (27,66 vs 27,68 con caché caliente); activarlo para toda tabla mixta rompía 32 tests, así que solo rellena tablas que mezclan clases o tienen ≥4 candidatos. *[estabilidad]*

Fuentes: 3d7ca311 2026-10-30, commit c1c457ec, commit e6925c47

<a id="native_f16"></a>
#### `NATIVE_F16` — ON, def. on

Razonamiento: Las conversiones f16-f32 eran secuencias enteras de ~40 instrucciones; un pixel shader de GTA de 1.242 instrucciones daba 3,66 MB de SPIR-V y ~14 s de compilación en NVIDIA. Con shaderFloat16 y controles de float de 16 bits el módulo baja a 0,91 MB y 1,5 s; la GPU ocupada pasa de 22 a 11 % con FPS igual (límite en CPU). El dispositivo decide si se activa y =0 lo apaga. *[ganancia]*

Fuentes: 3d7ca311 2026-10-08, commit 2267ddf5

<a id="ordered_lds_adds"></a>
#### `ORDERED_LDS_ADDS` — ON, def. on

Razonamiento: DS_ADD_RTN_U32 usaba un atómico por carril y el radix sort daba inversiones de índice con claves iguales; devolver los rangos en orden de carril del huésped (mitad baja antes de la alta) lo corrige (PR 26). Tras la fusión del 3-nov el interruptor quedó silenciosamente sin efecto en la ruta de GTA (lowering emparejado) y en grupos multionda, porque la puerta solo valía para wave32 o una onda emulada; se reabrió para pareadas y emulada no cooperativa. *[corrección]*

Fuentes: 3d7ca311 2026-11-03

<a id="pipeline_cache_write_async"></a>
#### `PIPELINE_CACHE_WRITE_ASYNC` — ON, def. on

Razonamiento: El checkpoint de cada 5 minutos exportaba y escribía la caché de 132 MB en el hilo de render dentro de PipelineCreation: una ventana de 5,4 s pagó 540 ms y 258 MB de asignaciones (Wrap copiaba el payload dos veces). La exportación sigue en el render, pero el fichero y el sello de prewarm se escriben en orden en un worker, sin la copia extra; el cierre espera, y los tests escriben en línea. *[ganancia]*

Fuentes: 3d7ca311 2026-11-06, commit 84ef6ac3, commit 9b3a2898

<a id="program_gate"></a>
#### `PROGRAM_GATE` — ON, def. on

Razonamiento: GetGraphicsPrograms/GetComputeProgram retenían ShaderPipelineCache._gate durante toda la consulta, materialización incluida: el frente perdía 6,1 ms/fotograma en estática y 7,7 en paneo. Los aciertos pasan a ConcurrentDictionary con scratch y caché de materialización por hilo; verify con 0 diferencias en 13,8 M (estática) y 11,4 M (paneo) de aciertos. Se descubrió de paso que la caché compartida ya devolvía rangos viejos de tablas de almacenamiento en dos compute shaders en paneo (~2.800 de 10 M), arreglable con GATE_TABLES=1 pero a ~1 FPS de coste en estática, así que esos planes siguen bajo el cerrojo por defecto. *[ganancia]*

**Contradicción:** Los tests 4220+939 en verde citados al integrar eran del build anterior a activarlo por defecto (el propio agente lo corrigió); no se pasaron sobre e1c6bc16 en ese momento.

Fuentes: 3d7ca311 2026-10-24, commit 675fb1fd, commit e1c6bc16

<a id="scratch_swizzle"></a>
#### `SCRATCH_SWIZZLE` — ON, def. on

Razonamiento: El recorrido de BVH apilaba en scratch con entrada x 256 + carril x 4 y el emulador indexaba un array privado con offset/4 mod 56: las entradas colisionaban y los pops devolvían punteros de otras entradas, con recorridos que no terminaban (rayos profundos, 33-50 %). Des-entrelazar el índice cuando su campo de carril coincide con el carril dejó los pushes a scratch de 12,5 M a 53; luego, con las aperturas FLAT corregidas, la entrada 14+L pisaba la 14 y se arregló tomando literales los índices dentro del scratch del propio carril (c857c888). *[corrección]*

**Contradicción:** Con el interruptor apagado el módulo ya no es idéntico byte a byte a la base tras c857c888 (cambia el SPIR-V de todo shader con scratch).

Fuentes: 3d7ca311 2026-10-08, 3d7ca311 2026-10-09, 3d7ca311 2026-10-10, commit c857c888

<a id="shader_max_steps"></a>
#### `SHADER_MAX_STEPS` — ON, def. 100000

Razonamiento: Hipótesis del 8-oct (investigación mega): el constructor de BVH 0x5981 costaba ~50 ms fijos por invocación porque sus bucles corrían hasta el guardián de 100.000 pasos. Con 10.000 pasó de 49,3 a 4,8 ms y el recorrido de 1.050 a 63 ms (Fidelidad 0,52→2,13 FPS), lo que probó que los shaders RT giraban hasta el guardián por un bug de traducción: el guardián enmascara errores, no es un parámetro a ajustar, y por eso se mantiene en 100000. *[corrección]*

Fuentes: 3d7ca311 2026-10-08

<a id="shader_prewarm"></a>
#### `SHADER_PREWARM` — ON, def. on

Razonamiento: Precompila al arrancar los pipelines de la lista guardada; el atasco 120/121 lo resolvió la PR 6. El 5-nov se vio que los parones de 0,3-9 s del «30 FPS 30 min» eran creaciones síncronas de vkCreateGraphicsPipelines (un pixel shader tiene 244 variantes) y que el banco ponía PREWARM=0 aunque el código lo trae encendido; con PREWARM=1 más la caché Vulkan en disco los tirones bajaron de 181 a 7 por sesión, a costa de ~12 s de espera al inicio, y se exige pasarlo explícito en los scripts. *[ganancia]*

Fuentes: 3d7ca311 2026-11-05

<a id="signed_zero_inf_nan"></a>
#### `SIGNED_ZERO_INF_NAN` — ON, def. on

Razonamiento: Se probó apagar SignedZeroInfNanPreserve (rt394sz) por si las comprobaciones de float costaban en el recorrido RT: 9,98 y 9,93 FPS con recorrido idéntico, sin ganancia. Se dejó encendido porque preserva la semántica sin coste medible; =0 queda solo como A/B. *[exacto-neutro]*

Fuentes: 3d7ca311 2026-10-10, commit 8e6f7be8

<a id="vk_pipeline_cache"></a>
#### `VK_PIPELINE_CACHE` — ON, def. auto (persistente también en Windows)

Razonamiento: Upstream (ed70f697) dejó la caché en memoria en Windows, suponiendo que la del driver y la lista de prewarm bastaban. En GTA un pixel shader tiene 244 variantes, así que sin caché persistente había 100-180 creaciones síncronas de 0,3-1,5 s por sesión (181 tirones); con el fichero en disco y el prewarm quedan 7-14 y se alcanzó 29,6-29,7 FPS en 35 min. Se invirtió el defecto en Windows en 7684da3d y =linux recupera el de upstream. *[A/B-upstream]*

Fuentes: 3d7ca311 2026-11-05, commit 7684da3d, commit ed70f697

<a id="volatile_glc_loads"></a>
#### `VOLATILE_GLC_LOADS` — ON, def. on

Razonamiento: Hipótesis del 8-oct (rt45int): en NVIDIA una carga SSBO sin coherent/volatile puede servirse de la L1 del SM y no ver el buffer_atomic_swap_x2 de otro grupo, con lo que el escaneo 0x35B8 gira hasta el guardián. Emitir las cargas con glc como OpLoad Volatile no cambió nada en 0x35B8 y tampoco arregla el test de tickets (falla también con Volatile), así que lo preciso fue GLC_ATOMIC_LOADS; Volatile quedó encendido como experimento local inofensivo. *[corrección]*

Fuentes: 3d7ca311 2026-10-08, 3d7ca311 2026-10-09

<a id="waterfall_null_image"></a>
#### `WATERFALL_NULL_IMAGE` — ON, def. on

Razonamiento: Un commit anterior (d8e51940) hizo que una lectura de descriptor en un offset derivado de V_READFIRSTLANE cayera a una imagen nula en vez de rechazar el plan; los tests de rechazo usan justo ese programa y el CI pasó a rojo. Se añadió =0 solo para esos tests y el fallback sigue encendido en juego; en la fusión del 3-nov los tests de upstream en conflicto recibieron override de flujo asíncrono. *[corrección]*

Fuentes: 3d7ca311 2026-10-11, 3d7ca311 2026-11-03, commit 51083d94

<a id="wave64_half_masks"></a>
#### `WAVE64_HALF_MASKS` — ON, def. on

Razonamiento: Hipótesis: el análisis de medias máscaras de la emulación de wave64 rompía el refit de BVH (0x5981 girando al guardián). =0 no cambió nada ni el 8-oct ni tras la fusión del 4-nov (fz04 2,94 FPS, sin mejora), así que se descartó como causa; sigue encendido (defecto de upstream) y tras la fusión del 3-nov vive en EmulatedWave64Lowering con el mismo defecto. *[otro]*

Fuentes: 3d7ca311 2026-10-08, 3d7ca311 2026-11-03, 3d7ca311 2026-11-04

<a id="async_graphics_pipelines"></a>
#### `ASYNC_GRAPHICS_PIPELINES` — OFF, def. 0

Razonamiento: Una sesión fuera del prólogo registró 95 avisos de creación lenta (250-1500 ms) con FPS 1-7; se implementó compilación de pipelines gráficos en worker saltando los draws pendientes, al estilo Ryujinx/yuzu, solo cuando el salto no deja efectos fuera de sus adjuntos (sin escrituras de memoria ni oclusión). Se dejó apagado en el código hasta medir; el modo 1 no salta nada en GTA (cuenta oclusión) y el 2 sí, pero necesita EXEMPT. *[sin-A/B]*

Fuentes: 3d7ca311 2026-11-06, commit d304709d

<a id="da_vsharp_oob"></a>
#### `DA_VSHARP_OOB` — OFF, def. off

Razonamiento: Sonda de la investigación de 0x260F (cadena de tablas de dispositivo que rompe el BVH): hipótesis de que los límites del V# con OOB_SELECT=3 (~2 % de los V#) se acotaban a records x stride en vez de los 3 bytes crudos. Con =1 el BVH sigue roto, igual que con las otras cuatro hipótesis probadas (stores sin seguir, orden entre lotes, stores a búfer no marcados, subida rancia), así que queda como sonda apagada. *[otro]*

Fuentes: 3d7ca311 2026-10-18, commit d5062a3d

<a id="exec_select_elimination"></a>
#### `EXEC_SELECT_ELIMINATION` — OFF, def. off

Razonamiento: Pase de upstream que elimina selects de EXEC sobre escrituras no observadas. Tras la fusión (rama con regresión ~0,7-3,5 FPS) se probó =0 (fz03) y no arregló nada; el 4-nov, junto a otros cinco defaults de upstream (gz05: 27,29 vs 26,56 FPS sin diferencia), se fijó el defecto a lo de 3368666f por el principio «defecto = lo nuestro», y el de upstream queda opt-in. Los tests de upstream que dependen de él lo activan con override. *[A/B-upstream]*

Daniel: Vía el coordinador: el principio de Daniel (defecto = lo nuestro, upstream opt-in) decide los seis defaults pendientes.

Fuentes: 3d7ca311 2026-11-04, commit 3fe66d3f

<a id="ps_live_exec"></a>
#### `PS_LIVE_EXEC` — OFF, def. off

Razonamiento: Máscara live-exec de upstream en pixel shaders; tras la fusión del 3-nov la rama rendía ~0,7-3,5 FPS y se buscó un interruptor culpable. gz05 con los seis defaults de upstream a lo nuestro dio 27,29 frente a 26,56: no explican la regresión, que es CPU del hilo de render. El 4-nov se fijó el defecto a lo de 3368666f (upstream opt-in) por el principio de Daniel «defecto = lo nuestro». *[A/B-upstream]*

Daniel: Vía el coordinador: el principio de Daniel (defecto = lo nuestro, upstream opt-in) decide los seis defaults pendientes.

Fuentes: 3d7ca311 2026-11-04, commit 3fe66d3f

<a id="runtime_call_records"></a>
#### `RUNTIME_CALL_RECORDS` — OFF, def. off

Razonamiento: Camino de upstream #1120 (6b7c1d50, 2026-10-23): programas de llamada de shader de UE4 más pequeños, una copia de callee por sitio y registros leídos en ejecución, compilados en segundo plano. Como los otros cinco defaults de upstream, se devolvió a lo de 3368666f (copia por registro) porque no mueve FPS (gz05 27,29 vs 26,56; gz10 todo nuestro 27,16) y por el principio de Daniel «defecto = lo nuestro». *[A/B-upstream]*

Daniel: Vía el coordinador: el principio de Daniel (defecto = lo nuestro, upstream opt-in) decide los seis defaults pendientes.

Fuentes: 3d7ca311 2026-11-04, commit 3fe66d3f, commit 6b7c1d50

<a id="coherent_buffers"></a>
#### `COHERENT_BUFFERS` — Descartado, def. off

Razonamiento: Hipótesis: los grupos del look-back de un scan de un paso (0x220A) leían filas rancias por no ser coherentes las cargas entre workgroups; se decoró todo el array de búferes como Coherent. En rt164int no arregló los árboles grandes y fue 1,47 FPS, así que se dejó apagado; la solución precisa fue solo cargas GLC atómicas (GLC_ATOMIC_LOADS). *[corrección]*

**Contradicción:** El registro dice que no arregla; sin embargo el 9-oct (15:40) se anota que COHERENT_BUFFERS=1 sí arreglaba el banco del ticket (solo rt164int, con árboles grandes, no mejoró).

Fuentes: 3d7ca311 2026-10-09

<a id="compute_pending_wait"></a>
#### `COMPUTE_PENDING_WAIT` — Descartado, def. off

Razonamiento: La fusión reintrodujo la condición de upstream (8ddf6601, #1130) por la que un cómputo sin call targets con compilación en curso se espera de forma síncrona dentro de los cerrojos. Se puso detrás de un interruptor con defecto «no listo» (como antes de la fusión, cuando el ejecutor sondeaba con Sleep(1)); ambos modos son exactos y no se integró porque en GTA V la condición no se ejecuta nunca, ni en frío ni en caliente. *[A/B-upstream]*

Fuentes: 3d7ca311 2026-11-05, commit 48c6e9ef

<a id="da_branchless"></a>
#### `DA_BRANCHLESS` — Descartado, def. off

Razonamiento: La consulta de dirección de dispositivo hace una rama para el límite de la tabla, otra para registrar el fallo (OR atómico) y luego la carga. Se probó elegir la entrada con select sin rama: exacto, 16,2 vs 16,5 ms de dispatch; solo quitando las dos ramas a la vez (DA_UNCHECKED, no exacto, cota inferior 13,3 ms) hay ganancia, y con FAULT_MODE=2/4 sigue sin ganar. *[exacto-neutro]*

Fuentes: 3d7ca311 2026-10-17, commit c8e09b20

<a id="da_fault_mode"></a>
#### `DA_FAULT_MODE` — Descartado, def. 0

Razonamiento: Hipótesis: el OR atómico por consulta que registra páginas sin mapear es el coste dominante. Modos: 1 y 3 sin registro (solo medida, no exactos; 14,9 ms), 2 guarda la página por invocación y la registra al final, 4 la registra en una rama sobre GroupNonUniformAny; los exactos no ganan en el juego (16,6-17,6 ms vs ~16,5). Más tarde se vio que el modo 2 coincide con la fusión por invocación de upstream (2cf37fe3d5). *[exacto-neutro]*

Fuentes: 3d7ca311 2026-10-17, 3d7ca311 2026-11-06, commit c8e09b20

<a id="gate_prefetch"></a>
#### `GATE_PREFETCH` — Descartado, def. 0

Razonamiento: El frente esperaba ~9,5 ms/fotograma en la puerta del pipeline; hipótesis: prelever fuera de la puerta las palabras GPU-sucias que la preparación sincroniza dentro. Funciona (espera de ~9,5 a ~0,2 ms) pero el render pasa a esperar ~0,5 ms más al frente y la estática baja ~0,6 FPS; repetido sobre 4f7fdcf8 el 22-oct (28,5→26,3: frente −4 ms, FlipDone +6) y descartado el 23-oct. *[adelanta-cuello]*

Fuentes: 3d7ca311 2026-10-17, 3d7ca311 2026-10-22, commit b34e0725

<a id="limits_in_shader_data"></a>
#### `LIMITS_IN_SHADER_DATA` — Descartado, def. off

Razonamiento: Los shaders de la cadena del BVH tienen límites de hilos pero sus datos caben en push constants, sin ShaderData, así que la GPU no puede escribir los límites y el dispatch indirecto los lee con una lectura síncrona en CPU. Forzar ShaderData en todo programa con límites eliminó esas lecturas pero dio 1,9 FPS por recompilar en cada dispatch (la clave de permutación no coincidía; al arreglar el emparejamiento seguía sin ganar), y se sustituyó por la variante solo para indirectos por hilos. *[coste-sonda]*

Fuentes: 3d7ca311 2026-10-10, commit 09b5e0e0

<a id="wave64_multiwave"></a>
#### `WAVE64_MULTIWAVE` — Descartado, def. no se lee

Razonamiento: Nuestra emulación de wave64 en grupos de varias ondas (PR 24: v_readlane de carril fijo, ranuras por onda y cita entre mitades) arregló el radix sort (0/2048 discrepancias, 0 registros −1) y se usó como base de los bancos RT con =1. Desapareció solo en la fusión del 3-nov (abe22246), sustituida por el lowering de upstream, que luego rompió el BVH hasta que se fijó WAVE64_PAIRED=0. *[corrección]*

**Contradicción:** El registro cita 764260b4 (13-oct) como retirada, pero el 17-oct (bcc53df7) el interruptor seguía vivo (off) y la fusión del 3-nov dice que lo elimina (abe22246).

Fuentes: 3d7ca311 2026-11-03, commit abe22246, commit bcc53df7

<a id="wave64_paired"></a>
#### `WAVE64_PAIRED` — Descartado, def. cómputo off / resto on

Razonamiento: Tras la fusión del 3-nov GTA caía a ~0,7 FPS: por defecto el cómputo wave64 multionda pasó al lowering emparejado de upstream en hosts de 32 carriles, que rompía los constructores de BVH y hacía que 0x8395 leyera listas más allá de su V# (ceros) y girara al guardián (193 ms por dispatch). Un agente fork probó la hipótesis con WAVE64_PAIRED=0 y 8395 bajó a 2-2,7 ms; el commit 96bd8554 devuelve el cómputo al emulado, con el emparejado opt-in. *[estabilidad]*

Fuentes: 3d7ca311 2026-11-03, commit 96bd8554

<a id="force_pixel_magenta"></a>
#### `FORCE_PIXEL_MAGENTA` — Sonda, def. off

Razonamiento: Sonda de upstream para separar falta de rasterización de ceros. Se comprobó que no era lo que producía el magenta de GTA: la causa raíz era que 0xD44E se saltaba por IncompatibleImageCandidates (relleno 2D MSAA en su tabla de materiales) y dejaba el marcador de 0x8395 sin sombrear; nunca estuvo fijado en un lanzamiento. *[otro]*

Fuentes: 3d7ca311 2026-11-05

<a id="shader_max_steps_hash"></a>
#### `SHADER_MAX_STEPS_HASH` — Sonda, def. vacío

Razonamiento: Herramienta de diagnóstico por shader nacida en rt163: el look-back de 0x220A chocaba con el guardián de 100k, y subirlo a 50 M para ese hash provocó TDR porque las esperas nunca terminan, lo que mostró que paciencia no era la solución. Se reutilizó para bajar el guardián de un solo hash (0x8395 a 20.000; 0x5981 para medir contra el guardián tras la fusión). *[otro]*

Fuentes: 3d7ca311 2026-10-09, 3d7ca311 2026-10-23, 3d7ca311 2026-11-03

### RT/BVH (38)

<a id="bvh_node_test"></a>
#### `BVH_NODE_TEST` — ON, def. gpurt

Razonamiento: Tras fusionar origin/main el test de nodo de upstream (#1061) pasó a ser el defecto y cambiaba salida y coste de 0x8395. Se devolvió a gpurt porque los 7,1 a 4,5 ms y 25,9 a 28,9 FPS se midieron con la matemática antigua, y gz05/gz10 mostraron que los seis defectos de upstream pendientes no mueven FPS. *[A/B-upstream]*

Daniel: Principio de Daniel, citado por el coordinador (no literal): ante un defecto de upstream en disputa, el defecto es lo nuestro (lo medido); los tests de upstream activan su camino con override.

Fuentes: 3d7ca311 2026-11-03, 3d7ca311 2026-11-04, commit 3fe66d3f

<a id="bvh_triangle23"></a>
#### `BVH_TRIANGLE23` — ON, def. on

Razonamiento: Al construir el prototipo de ray query por hardware y compararlo, se vio que el test de nodo emulado solo hacía los triángulos tipo 0 y 1; el 2 y el 3 devolvían cuatro dwords inválidos que el bucle de 8395 aceptaba como candidato con t=NaN (2,7 G de 4,6 G impactos comparados). Corregirlo dio más que el RT por hardware: se encendió por defecto. *[corrección]*

Fuentes: 3d7ca311 2026-10-30, commit 0c2fbcf8, commit 15e1df22

<a id="bvh_tri_degenerate"></a>
#### `BVH_TRI_DEGENERATE` — ON, def. 1

Razonamiento: Tras TRIANGLE23 quedaba un 0,8 % de impactos con t=NaN; la sonda confirmó la hipótesis tDenom=0: 718 M de 9,42 G impactos (7,6 %) son triángulos de área nula (datos del constructor). Rechazarlos es exacto contra el ray query HW como oráculo y no cambia el FPS, así que va por defecto; además destapó que muchos A/B de paneo se medían con D44E saltado. *[corrección]*

Fuentes: 3d7ca311 2026-10-30, commit 7f3f0832, commit ae9d2fea

<a id="bvh_unified_fetch"></a>
#### `BVH_UNIFIED_FETCH` — ON, def. 3

Razonamiento: Hipótesis: la latencia de carga dependiente (búsqueda de página repetida por rama de tipo, dos veces en caja float32) domina el recorrido. El intento de hito 4 (11-10) fue inconcluso (11,59 ms en una sesión, indistinguible del modo lento); la versión exacta con modos 1-3 ganó en el A/B del 16-10 (con spill, p174 8,08 a 6,88) y quedó =3. *[ganancia]*

Fuentes: 3d7ca311 2026-10-11, 3d7ca311 2026-10-16, 3d7ca311 2026-10-22, commit 6b9881c8

<a id="bvh_vector_loads"></a>
#### `BVH_VECTOR_LOADS` — ON, def. on

Razonamiento: Hipótesis: leer cada unidad de nodo como 4 vectores de 16 B reduciría el coste del recorrido. Resultado dentro del ruido (23,7 a 22,9 ms): el coste es latencia de memoria por nodo, no número de instrucciones de carga. El motivo de dejarlo encendido no consta (inocuo y exacto). *[exacto-neutro]*

Fuentes: 3d7ca311 2026-10-10

<a id="native_blas_loop"></a>
#### `NATIVE_BLAS_LOOP` — ON, def. on

Razonamiento: El bucle BLAS de 0x8395 traducido como wave pagaba la contabilidad de EXEC de los 64 carriles en cada iteración y, con wave64 emulado, un intercambio con barrera de workgroup por cada salto de EXEC hasta que acaba el carril más lento. Traducirlo por carril, con verify bit a bit (884/884 ventanas), bajó el pase de 23,5 a 14,6 ms. *[ganancia]*

Fuentes: 3d7ca311 2026-10-10, commit eddbad28, commit f8ae54b1

<a id="native_tlas_loop"></a>
#### `NATIVE_TLAS_LOOP` — ON, def. on

Razonamiento: El recorrido del TLAS es uniforme por wave y gastaba ~40 intercambios wave64 con barrera por iteración. Con ballots y shuffles por mitad de 32 carriles se deja uno por nodo caja, con verify exacto (985/985); el pase bajó de 14,8 a 13,8 ms aunque el FPS quedó dentro del ruido. *[ganancia]*

Fuentes: 3d7ca311 2026-10-10, commit 454378fc, commit 6ba8da83

<a id="rt_shared_stacks"></a>
#### `RT_SHARED_STACKS` — ON, def. on

Razonamiento: Hipótesis: NVIDIA mantiene en registros los arrays privados con índice dinámico (las pilas BLAS/TLAS), lo que dejaba 0x8395 en 255 registros y 8 warps por SM. Pasarlos a memoria de workgroup bajó a 180 registros y de 13,9 a 10,5 ms con verify 0 diferencias; es inferencia, pues NVIDIA no devuelve el ensamblador, y luego STACK_SPLIT la refinó. *[ganancia]*

Fuentes: 3d7ca311 2026-10-11, commit d510f183

<a id="rt_spill_scalars"></a>
#### `RT_SPILL_SCALARS` — ON, def. on

Razonamiento: El juego mantiene vivos 34 escalares a lo largo de todo el recorrido sin tocarlos y NVIDIA los guarda en registros vectoriales (por carril). Aparcarlos en el área de pila RT lleva 0x8395 de 168 a 128 registros y de 12 a 16 warps; la prueba fue sobrescribirlos tras el recorrido (con pérdida): lo que cuenta es lo vivo a través del recorrido, no recortar instrucciones. *[ganancia]*

Fuentes: 3d7ca311 2026-10-16, commit 386e5ef9, commit 6b9881c8

<a id="rt_stack_split"></a>
#### `RT_STACK_SPLIT` — ON, def. 8

Razonamiento: Con toda la pila BLAS en memoria compartida (23 KB por grupo) el driver dimensionaba 180 registros y 8 warps por SM, porque el presupuesto de registros sigue a la ocupación que permite la memoria de workgroup. Partir la pila en tres niveles baja a 6,9 KB y 128 registros (10,42 a 7,4-8,6 ms), bit a bit; mover solo la pila BLAS no bastaba. *[ganancia]*

Fuentes: 3d7ca311 2026-10-11, commit f34848e7

<a id="scratch_slots"></a>
#### `SCRATCH_SLOTS` — ON, def. on

Razonamiento: Misma idea que las pilas, aplicada al scratch de los compute: un array privado con índice dinámico lo paga NVIDIA en registros, así que se mueve a la ranura de warp del área RT. 0x9FBC pasa de 168 a 127 registros y de 1,30 a 1,01 ms; el verify no ejercitó ninguna carga de scratch real en el banco, por lo que la exactitud queda pendiente de =2 en partida. *[ganancia]*

Fuentes: 3d7ca311 2026-10-11, commit a30f43d8

<a id="skip_rt"></a>
#### `SKIP_RT` — OFF, def. off

Razonamiento: Viene de la PR #1061 de upstream: salta los dispatches con ImageBvh*IntersectRay. Se propuso traerlo como brazo de control A/B «GTA sin RT»; no consta que se haya medido. *[sin-A/B]*

Fuentes: 3d7ca311 2026-10-10

<a id="bvh_lean_slabs"></a>
#### `BVH_LEAN_SLABS` — Descartado, def. 0

Razonamiento: Prueba de slabs sin max3/min3 con NaN. Al principio se creyó no exacto (el driver pierde el NaN sin SignedZeroInfNanPreserve), pero el juego compila con ese modo y coincide en todos los carriles; el verify =2 dio 0 nodos distintos en ~185 G. Gana 0,05-0,35 ms, por debajo del umbral de 0,3 ms, y se queda apagado. *[exacto-neutro]*

Fuentes: 3d7ca311 2026-10-16, 3d7ca311 2026-10-30, commit caa57863

<a id="bvh_page_cache"></a>
#### `BVH_PAGE_CACHE` — Descartado, def. off

Razonamiento: Hipótesis: el coste del recorrido es la búsqueda en la tabla de páginas por cada unidad de 64 B, así que se probó cachear la entrada por invocación. Salió neutra y no se adoptó; además la varianza entre sesión rápida y lenta (~10 %) hace indecidibles las diferencias con una sola sesión por brazo. *[exacto-neutro]*

Fuentes: 3d7ca311 2026-10-10, 3d7ca311 2026-10-11

<a id="force_bvh_counts"></a>
#### `FORCE_BVH_COUNTS` — Descartado, def. off

Razonamiento: Experimento para saber si a las cabeceras BVH solo les faltaban las cuentas: volcado de 256 B antes y después del dispatch de 0x56D6 para distinguir entre «algo las borra después» y «la escritura del shader no llega», y parche forzado de las cuentas. Mostró que la escritura sí aterriza; la pista de cuentas a 0 se retractó. *[otro]*

Fuentes: 3d7ca311 2026-10-09

<a id="refit_header_guard"></a>
#### `REFIT_HEADER_GUARD` — Descartado, def. off

Razonamiento: Hipótesis de causa del giro RT: el refit 0x743E escribía registros de nodo sobre las unidades 0-3 (la cabecera) de 56/86 BLAS, dejando las cuentas a 0 y la cadena de pares sin fin. La guarda lo confirmó (0/85 cabeceras borradas, recorrido 716 a 50 ms); era una prueba de causalidad, sustituida por el arreglo real del EXEC de entrada en la PR23. *[corrección]*

Fuentes: 3d7ca311 2026-10-09

<a id="repair_bvh_headers"></a>
#### `REPAIR_BVH_HEADERS` — Descartado, def. off

Razonamiento: Hipótesis de que las cabeceras BVH llegaban a 0 y bastaba copiar los 256 B del huésped a la copia GPU. En rt98int hubo 24 reparaciones, la misma base 13 veces, y la cabecera seguía leyéndose a 0: no confirmó el diagnóstico y la pista se retractó. *[otro]*

Fuentes: 3d7ca311 2026-10-09

<a id="rt_rayquery"></a>
#### `RT_RAYQUERY` — Descartado, def. off

Razonamiento: Se estimó que un ray query por hardware (VK_KHR_ray_query) ahorraría 4-5 ms en 8395, la palanca grande que quedaba. El prototipo resultó viable y casi exacto, pero solo -0,6/-0,7 ms sobre la emulación corregida; la ganancia grande de esa fase salió de otro sitio, el fallo de los triángulos 2/3, y se aparcó. *[exacto-neutro]*

Fuentes: 3d7ca311 2026-10-30, 3d7ca311 2026-10-31, commit cdadb18a

<a id="tlas_hle"></a>
#### `TLAS_HLE` — Descartado, def. 0

Razonamiento: Hipótesis: construir el TLAS en el host y saltar la cadena del juego quita lecturas síncronas (B198, 02F1) y ~68 dispatches por fotograma. Es exacto (verify 0 diferencias) pero ganó poco (23,6 a 24,6 FPS) y colgó la GPU al interceptar cadenas n=8/n=14 que reutilizan un anillo por fotograma. RECENT lo estabilizó, pero solo quita ~1,9 ms de readbacks; las esperas grandes son de la cadena BLAS y se aparcó apagado. *[estabilidad]*

**Contradicción:** Registro y commit dicen 5 DeviceLost en ~13 sesiones; un resumen de compactación del 23-10 dice 6 de ~14.

Fuentes: 3d7ca311 2026-10-22, 3d7ca311 2026-10-23, 3d7ca311 2026-10-24, commit 6a260857

<a id="tlas_hle_belongs"></a>
#### `TLAS_HLE_BELONGS` — Descartado, def. on

Razonamiento: Hipótesis: el cuelgue venía de saltar pasadas que no pertenecen a la cadena; se restringió el salto a la clave, la cabecera o los nodos del TLAS de este fotograma y se cierran las ventanas obsoletas. No bastó: [TLAS_BELONGS] no mostró ningún RUN por KeyBelow/Blocks/Instances, los saltos previos eran correctos. *[estabilidad]*

Fuentes: 3d7ca311 2026-10-23, commit 89c77795

<a id="tlas_hle_learn"></a>
#### `TLAS_HLE_LEARN` — Descartado, def. on

Razonamiento: Intento de estabilizar el TLAS HLE: interceptar una cabecera de trabajo solo tras 8 construcciones limpias y excluir las que escriben fuera. Aun así v11Pa cayó con DeviceLost: el modo 1 salta algo que el sondeo de V# no ve (el área de trabajo compartida 0x60A36xxxx). La variante exacta (decidir por construcción entera antes del primer salto) no es posible porque el primer salto es el propio 0xB198. *[estabilidad]*

Fuentes: 3d7ca311 2026-10-23, commit 9912e980

<a id="tlas_hle_recent"></a>
#### `TLAS_HLE_RECENT` — Descartado, def. on

Razonamiento: Causa de los cuelgues: el TLAS y las áreas de trabajo viven en un anillo por fotograma que el juego reutiliza, y el conjunto creciente de bases recorridas dejaba al host saltar pasadas de cadenas que luego contenían estructuras de la familia BLAS. Con caducidad de 128 ticks y misma n, 12/12 sesiones limpias, pero sin ganancia de FPS. *[estabilidad]*

Fuentes: 3d7ca311 2026-10-24, commit 3e28a1bb

<a id="tlas_hle_traversed_only"></a>
#### `TLAS_HLE_TRAVERSED_ONLY` — Descartado, def. on

Razonamiento: Las cadenas n=8/n=14 interceptadas por las tablas de instancias de 0x994A tienen áreas de trabajo e interiores que leen programas ajenos, y las pasadas saltadas los dejarían rancios; por eso solo se construye el TLAS que recorre 0x8395. Se creó sin validar y, con RECENT, resultó estable pero sin ganancia. *[estabilidad]*

Fuentes: 3d7ca311 2026-10-23, commit 29f26098

<a id="tlas_hle_verify_every"></a>
#### `TLAS_HLE_VERIFY_EVERY` — Descartado, def. 4

Razonamiento: Perilla del modo verify (=2) del TLAS HLE: compara una de cada k construcciones con el árbol del juego (k=4 por defecto, =1 compara todas). El motivo de elegir 4 no consta. *[otro]*

Fuentes: commit f59fa49f

<a id="8395_profile"></a>
#### `8395_PROFILE` — Sonda, def. off

Razonamiento: Sonda de reloj de subgrupo (VK_KHR_shader_clock, habilitada solo bajo el interruptor) por regiones del pase de reflejos RT 0x8395, hito 2 parte 1, para saber dónde optimizar antes de tocar el recorrido. La parte 2 (hacer nativo el bucle TLAS) no se implementó a propósito: sin medida previa y el bucle no puede hacerse nativo por carril con exactitud de bits. *[coste-sonda]*

Fuentes: 3d7ca311 2026-10-10, commit a657739f

<a id="blas_cycle"></a>
#### `BLAS_CYCLE` — Sonda, def. 0

Razonamiento: Trampa para el modo lento (GPU al 100 %, 0,3–3 FPS), con la hipótesis de un ciclo en el grafo BVH: marca de Brent sobre cajas desde 256 iteraciones en el bucle BLAS de 0x8395, con mapa de BLAS largos o con revisitas que el host recorre desde la raíz. El modo =2 costaba ~1,4 ms por dispatch de 8395 (6,32 vs ~4,9), por eso se desligó de SLOW_MODE_PROBE y se añadió =3 como trampa solo con contadores de salida; el modo lento no se reprodujo en 32 sesiones. *[modo-lento]*

Fuentes: 3d7ca311 2026-10-31, commit a92ddbaa, commit b2f6a735

<a id="blas_cycle_from"></a>
#### `BLAS_CYCLE_FROM` — Sonda, def. 256

Razonamiento: Subparámetro de la trampa BLAS_CYCLE (la hipótesis del modo lento era un BLAS con ciclo): se usó con =1 para comparar la rama antigua con la fusión (bx09/bx10) mientras se buscaba por qué la fusión iba a ~0,7 FPS. *[modo-lento]*

Fuentes: 3d7ca311 2026-11-03

<a id="blas_cycle_long"></a>
#### `BLAS_CYCLE_LONG` — Sonda, def. 4096

Razonamiento: Fija el umbral de «recorrido largo» de 8395 (4096 pasos; los sanos andan por debajo de ~650) para la trampa del modo lento, cuya hipótesis era un BLAS con ciclo. Probado en vivo con 300: 1,25 M recorridos largos y 4 blobs analizados, sin coste visible (8395/9FBC 5,23 frente a 5,08-5,26). *[modo-lento]*

Fuentes: 3d7ca311 2026-10-31, commit 703c2478

<a id="bvhl_probe"></a>
#### `BVHL_PROBE` — Sonda, def. off

Razonamiento: Para cazar quién escribe «PSR_BVHL» en el pool de V# del juego (crash del pasillo rojo, 2/8). Con la sonda 0/3 crashes, pero con p=0,25 tres limpias ocurren un 42 % por azar: es «no observado», no «arreglado». Hipótesis abiertas: escritor CPU del juego con puntero malo, free() ajeno o carrera del instante del crash. *[estabilidad]*

Fuentes: 3d7ca311 2026-11-05, commit bd035ddf, commit c17d9203, commit f26af214

<a id="bvh_probes"></a>
#### `BVH_PROBES` — Sonda, def. off

Razonamiento: Un muestreo de CPU del hilo de render mostró que las sondas [local] de la investigación BVH seguían encendidas (330.000 líneas de traza y ~6.500 volcados por ejecución, más lecturas de entorno y cerrojos en caminos calientes). Detrás del interruptor: 7,60 a 8,25 FPS y DescriptorPreparation 16 a 6,8 ms. *[coste-sonda]*

Fuentes: 3d7ca311 2026-10-10, commit fdee3941

<a id="bvh_tri_probe"></a>
#### `BVH_TRI_PROBE` — Sonda, def. off

Razonamiento: Sonda para comprobar la hipótesis de t=NaN residual (tDenom=0) y motivó TRI_DEGENERATE; además confirmó con registros de índices (40/0 y 48/0 aciertos) que el orden de vértices de los triángulos 2/3 es correcto y no hay que cambiarlo. *[corrección]*

Fuentes: 3d7ca311 2026-10-30, commit ae9d2fea

<a id="key_probe"></a>
#### `KEY_PROBE` — Sonda, def. off

Razonamiento: Hipótesis «causa 3»: las claves Morton de los BLAS dinámicos eran 0 porque las cotas que 0x4481 lee por s_load estaban rancias; rt188/rt189 vieron ceros o basura en ambas copias. Después se descartó: las cotas son 0 de verdad, porque 0xF1D1 las calcula así en la primera construcción (volcado GPU y banco LocalBoundsReductionTests exactos). *[otro]*

**Contradicción:** El registro cita rt188/rt189 como «cotas rancias, causa 3» (bitácora L716), pero la bitácora L718 la descarta: las cotas son 0 de verdad.

Fuentes: 3d7ca311 2026-10-09, 3d7ca311 2026-10-12, 3d7ca311 2026-10-17

<a id="tlas_hle_consumers"></a>
#### `TLAS_HLE_CONSUMERS` — Sonda, def. off

Razonamiento: Sonda para saber qué programas leen, desde fuera de la cadena, el área de trabajo o el interior de un TLAS construido. Reveló que las cadenas n=8/n=14 tienen áreas leídas por 0x10CD, 0x6A53, 0xAACC, 0x5981, 0xF43B y 0xCC38, lo que motivó TRAVERSED_ONLY; además, tomar los valores de nuestra construcción no vale, porque las pasadas del juego reescriben el TLAS. *[estabilidad]*

Fuentes: 3d7ca311 2026-10-23, commit 3c375358, commit 3fac434b

<a id="tlas_hle_poison"></a>
#### `TLAS_HLE_POISON` — Sonda, def. off

Razonamiento: Prueba de imagen para cerrar si alguien fuera de la cadena lee +0x8C/+0x90 de la cabecera TLAS: con 0xDEADBEEF la diferencia fue 0,5-2,1 % de píxeles, igual que el ruido de =1 contra =1 (1,5-2,4 %), así que se dio por cerrado que nadie los lee. *[otro]*

**Contradicción:** El registro dice «sin medir», pero el 22-10 se midió: diferencia de imagen 0,5-2,1 % frente a un ruido de 1,5-2,4 %, concluyendo que nadie los lee.

Fuentes: 3d7ca311 2026-10-22, commit 9a363185

<a id="tlas_hle_writes"></a>
#### `TLAS_HLE_WRITES` — Sonda, def. off

Razonamiento: Diagnóstico en modo 2 para ver qué escriben los dispatches que el HLE saltaría. El primer resultado no halló blobs PSR_BVHL pero sí escrituras fuera del TLAS del fotograma; al ampliar la búsqueda hacia atrás se vio que las cadenas n=14 escriben dentro de otros blobs (D5D0 en 0x664294E00). *[estabilidad]*

Fuentes: 3d7ca311 2026-10-23, commit 7374985a, commit 87878d26

<a id="track_build"></a>
#### `TRACK_BUILD` — Sonda, def. off

Razonamiento: Sirve para seguir las construcciones de BLAS dinámicos y volcar cada etapa. Se usó para comparar la rama antigua con la fusión (3,6-4,8 FPS): las primeras salidas que difieren son CA99, 4505 (ranuras permutadas) y 4F1F (contador 0x6CC frente a 0x6CA), con FADB/B728 como primeras etapas sospechosas. Tras la fusión exige además BVH_PROBES=1. *[otro]*

Fuentes: 3d7ca311 2026-11-04, commit bab71afa, commit c0c3a2c8, commit cc44d752

Sin razonamiento encontrado (2): `BVH_TRIANGLE_RANGE`, `RQ_DUMP`.

### scheduling/front/render (68)

<a id="attach_repeat"></a>
#### `ATTACH_REPEAT` — ON, def. 1

Razonamiento: Adquirir los attachments de cada draw costaba ~2,5 ms/frame y ~80 % de los draws repetían los mismos destinos. Un draw con los mismos targets resueltos, mismo tick, mismo ámbito abierto y mismo StateVersion por backing (no una generación global, que bajaba las repeticiones al 37 %) reutiliza la adquisición anterior; exacto, =2 verifica. El coordinador lo activó por defecto el 2026-10-18. Tras la fusión, la memoria in-place de upstream solo repetía la mitad (584 vs 270 adquisiciones/frame), así que se portó al RenderRecorder. *[ganancia]*

Fuentes: 3d7ca311 2026-10-18, commit 219ddb7b, commit 63437244

<a id="attach_report_tick"></a>
#### `ATTACH_REPORT_TICK` — ON, def. on

Razonamiento: Recorte exacto del paquete gta/perdraw: los QPC por draw del informe de 5 s solo se toman si el informe está activo (en este caso, leer TickCount64). Tras la fusión de 2026-11-03 la lectura quedó inerte (el stub ya no imprime la línea [ATTACH_REPEAT]). *[coste-sonda]*

**Contradicción:** Tras la fusión del 2026-11-03 el interruptor se sigue leyendo pero es inerte (ReportAttachRepeat sin llamar); el registro lo da como activo sin matiz.

Fuentes: 3d7ca311 2026-10-25, 3d7ca311 2026-11-03

<a id="backing_alias"></a>
#### `BACKING_ALIAS` — ON, def. on

Razonamiento: Hipótesis: la caché de lectura limpia por alias del backing (puntero por página virtual) quedaba rancia cuando el juego liberaba y reasignaba la página, dando ceros en 0x56D6. =0 en rt110int no cambió nada, así que el alias no era la causa. Tras la fusión con upstream, =0 llegó a no hacer nada (se quitó el ámbito de alias) y se restauró vía TryGetAliasPointer. *[corrección]*

Fuentes: 3d7ca311 2026-10-09, 3d7ca311 2026-11-03

<a id="back_queue_capacity"></a>
#### `BACK_QUEUE_CAPACITY` — ON, def. 16384

Razonamiento: El comentario de upstream (#1077) citaba 64 como óptimo, pero allí es la cola de su propio command thread; la nuestra cuenta ítems del front (~3 k por frame, profundidad media 1.350-4.000 de 16.384), otras unidades. Con 1024 las 7 sesiones fueron más lentas que todas las de base (estática -1,04, paneo -1,46); 64 ya había medido peor (9,67 vs 10,08). Se queda 16384. *[sin-A/B]*

Fuentes: 3d7ca311 2026-10-10, 3d7ca311 2026-10-13, commit e828ff78

<a id="buffer_size_reuse"></a>
#### `BUFFER_SIZE_REUSE` — ON, def. 1

Razonamiento: ClampMappedSize se llamaba ~19 k veces por frame y 8,5 k eran repetidas: BindStorageBuffer reutiliza el tamaño que FindBuffers ya recortó si siguen iguales la época del memo y la generación de mapeo. Exacto (verify =2, 0 discrepancias) y parte del paquete perdraw (-2,06 ms de render gestionado). Tras la fusión, el modo 1 pasó a ser la reutilización de upstream y 0/2 pasan por la nuestra. *[ganancia]*

Fuentes: 3d7ca311 2026-10-24, 3d7ca311 2026-10-25, 3d7ca311 2026-11-03

<a id="dispatch_barriers"></a>
#### `DISPATCH_BARRIERS` — ON, def. on

Razonamiento: Hipótesis: quitar las barreras antes y después de cada dispatch (dejando que ACQUIRE_MEM y los eventos de flush emitan la global) aligeraría la GPU. Es correcto (BLAS limpio, 0 ciclos) pero no ganó porque la GPU no era el límite en esa fase; la tanda sondas1 R8 lo midió peor y se planeó repetirlo con la GPU ocupada. Tras la fusión se volvió a conectar el control en RenderRecorder.Compute. *[exacto-neutro]*

**Contradicción:** El registro dice sin ganancia; la tanda sondas1 del 2026-10-16 anota DISPATCH_BARRIERS=0 como peor.

Fuentes: 3d7ca311 2026-10-10, 3d7ca311 2026-10-16, 3d7ca311 2026-11-03, commit 8e6f7be8

<a id="draws_per_batch"></a>
#### `DRAWS_PER_BATCH` — ON, def. 64

Razonamiento: Tamaño de lote para sellar y enviar. Los tamaños pequeños (16/64, 32/128) no ganan, y bajar _PASS a 128 o 48 no cambia esperas ni huecos (26,32 y 26,70 FPS): el tamaño del tick no era la palanca. El envío ya llega justo a tiempo (TryDownloadAsync hace el Flush; 0,06 ms entre Seal y Submitted), con cota de ganancia de ~0,4 ms/frame. *[exacto-neutro]*

Fuentes: 3d7ca311 2026-10-17, 3d7ca311 2026-10-29, commit e4e7ee79

<a id="draw_timing_gate"></a>
#### `DRAW_TIMING_GATE` — ON, def. on

Razonamiento: Quitar del camino por draw las lecturas de QPC cuando su línea de informe no está activa, dentro del paquete exacto gta/perdraw (-2,06 ms de render gestionado). Tras la fusión el gate se llevó también a RunDeferredWork. *[coste-sonda]*

Fuentes: 3d7ca311 2026-10-25, 3d7ca311 2026-11-03, commit c5813da2

<a id="dynamic_state_filter"></a>
#### `DYNAMIC_STATE_FILTER` — ON, def. on

Razonamiento: Cada draw hacía 9-15 llamadas vkCmdSet* aunque nada cambiara; en Vulkan ese estado persiste dentro del command buffer y todos los pipelines del guest declaran los mismos estados dinámicos. Se graba cada grupo solo si difiere de lo último grabado, invalidando con command buffer o tick nuevo y con el blit color→profundidad (su pipeline tiene estado estático). Se conservaron capturas del A/B por si un estado rancio dejaba artefactos; la auditoría del 2026-11-07 lo marca seguro en workers. *[ganancia]*

Fuentes: 3d7ca311 2026-10-11, 3d7ca311 2026-11-07, commit 2a88bd40

<a id="front_defer_indirect"></a>
#### `FRONT_DEFER_INDIRECT` — ON, def. on

Razonamiento: Las lecturas de memoria de comandos del front eran dispatches indirectos en unidades de hilo (el intérprete leía los contadores en CPU). Ahora el front los encola y el render lee los contadores sincronizados al ejecutarlos: 7,26 FPS con 0 lecturas de comandos en el front. Fue sospechoso de los ciclos BVH, pero =0 no los quitó. *[ganancia]*

Fuentes: 3d7ca311 2026-10-09, 3d7ca311 2026-10-10

<a id="front_defer_prep"></a>
#### `FRONT_DEFER_PREP` — ON, def. on

Razonamiento: Una preparación en el front que necesita leer datos escritos por GPU no puede esperar al hilo de render; se descarta con una excepción de diferido y el render prepara el programa al ejecutar el draw o dispatch, en orden del stream. Se añadió en la primera arquitectura front/render por corrección; el trabajo de preparación del front llegó a costar ~15 ms/frame. *[corrección]*

Fuentes: 3d7ca311 2026-10-09, 3d7ca311 2026-10-23

<a id="front_pending_labels"></a>
#### `FRONT_PENDING_LABELS` — ON, def. on

Razonamiento: El front pasaba ~20 ms/frame bloqueado en WAIT_REG_MEM sobre etiquetas cruzadas gráficos↔cómputo que el render aplicaba 6-11 ms después de encolarlas. Hacer que las esperas del front vean las escrituras ya encoladas ganaba FPS pero congelaba la intro (~3 de 4 estáticas) por lecturas rancias del front y carreras en diccionarios compartidos; se corrigió con FRONT_PREP_CHECK y estructuras concurrentes. La pérdida previa en paneo era de antes de la máscara de colas; se encendió el 2026-10-13 con 0 fallos en 18 sesiones. *[ganancia]*

Fuentes: 3d7ca311 2026-10-12, 3d7ca311 2026-10-13, commit 4f5bdd78, commit d0bf68cb

<a id="front_prep_check"></a>
#### `FRONT_PREP_CHECK` — ON, def. on

Razonamiento: El front lee memoria guest directo solo donde el render no ha registrado escritura GPU, pero va por delante: con PENDING_LABELS una cola pasa su espera antes de que el render grabe el trabajo de la otra, y el sondeo vio 20-30 lecturas rancias byte-exactas por sesión en los argumentos de los dispatches colgados. Un contador de versión de primeras escrituras GPU descarta la preparación si cambió. El control con PREP_CHECK=0 y las correcciones de carrera tampoco colgó (0/8), así que las carreras explican la caída a cero y el check se dejó por ser incorrecto por construcción sin coste medible. *[corrección]*

**Contradicción:** Las 8 sesiones de control con PREP_CHECK=0 y las carreras corregidas no colgaron, así que el efecto del check sobre los cuelgues no está aislado; el registro lo presenta como la solución.

Fuentes: 3d7ca311 2026-10-13, commit 85a17ed7

<a id="front_prep_ranges"></a>
#### `FRONT_PREP_RANGES` — ON, def. 1

Razonamiento: En paneo el streaming añade rangos GPU-escritos cada frame y cada uno subía la versión global, descartando todas las preparaciones del front en vuelo (11,4/frame) que el render rehacía (+0,86 ms de materialización). Ahora el front anota las páginas de 4 KB que lee y la caché guarda un anillo de rangos de las últimas 1024 versiones; solo se descarta si hay solape. Ahorro real ~0,1 ms; la ganancia de paneo (25,98 vs 25,13) la explica casi entera una sesión de 27,78: se acepta por exacto y no empeorar, no por FPS. *[exacto-neutro]*

**Contradicción:** El registro presenta paneo 25,98 vs 25,13 como resultado; el agente dice que lo explica casi entero una sola sesión y que se acepta sin ganancia de FPS.

Fuentes: 3d7ca311 2026-10-29, commit 8e5387b7, commit e2eff0fa

<a id="front_skip_deferring"></a>
#### `FRONT_SKIP_DEFERRING` — ON, def. on

Razonamiento: Las preparaciones del front que salían vacías costaban 1,2-2,1 ms/frame; si las últimas 4 de un programa salieron vacías, se deja al render (con un reintento de cada 32) y el render las prepara como cualquier preparación vacía, bajando ese coste a 0,07 ms. Es exacto y junto con REGISTER_COPY_DIRECT recorta 2,4-3,8 ms del front, aunque sin FPS significativo; se activó el 2026-10-13 con REBASE. *[exacto-neutro]*

Fuentes: 3d7ca311 2026-10-12, 3d7ca311 2026-10-13, commit 6cacd7d9

<a id="front_write_overlay"></a>
#### `FRONT_WRITE_OVERLAY` — ON, def. 3

Razonamiento: El front quedaba bloqueado en ~400 WriteGuestNow por 5 s (cada uno vaciando ~1.500 elementos). Con el modo 1 (parchear lecturas con los bytes pendientes) el front quedaba libre pero BLAS salía con árboles con ciclos en ~3 de 4 ejecuciones (~60 de ~210 blobs; los rayos giraban hasta el guardián de 100 k pasos): la bimodalidad era un bug de corrección, no de rendimiento. El modo 2 colgaba la intro (bloqueo de lectura del back durante la preparación); el modo 3 difiere esa preparación al render y dio 0 ciclos en 3/3 y ~+1,5 %. *[corrección]*

**Contradicción:** El registro ya señala la incoherencia con la memoria/bitácora (apagada, opcional =1): las transcripciones muestran que esa fue la mitigación del 2026-10-10 mañana y que el modo 3 por defecto (09b5e0e0) la superó el mismo día.

Fuentes: 3d7ca311 2026-10-09, 3d7ca311 2026-10-10, commit 09b5e0e0, commit aaeed674

<a id="gpu_thread_indirect"></a>
#### `GPU_THREAD_INDIRECT` — ON, def. on

Razonamiento: Los dispatches indirectos en unidades de hilo obligaban a leer los contadores en CPU; se resuelven en GPU con vkCmdDispatchIndirect. Hoy es la vía por defecto de D9800 (TryDispatchIndirectThreads) en lugar de DispatchIndirectDeferred, y los 530-700 por frame vuelven del worker como FrontDispatch tomado, rompiendo el segmento de los workers paralelos; de ahí SLICE_THREAD_INDIRECT, opt-in. *[ganancia]*

Fuentes: 3d7ca311 2026-11-03, 3d7ca311 2026-11-07, commit 66e8d8bc

<a id="guest_poll_backoff"></a>
#### `GUEST_POLL_BACKOFF` — ON, def. 16

Razonamiento: Contención de CPU: el proceso usaba ~603 % de una CPU lógica con hilos Subrender haciendo usleep/yield cortos en ráfaga (hasta ~4x52 % de un núcleo) en un i7-6900K de 8 núcleos. Tras N llamadas cortas seguidas, dormir 1 ms en vez de Thread.Yield libera núcleos; el ahorro es 0 si no hay tormenta de yields. En la prueba del paquete S (sin helper lanes) bajó 41 core-ms/frame y subió 0,9 FPS; con el defecto por criterio de ms se integró en ef5fb944 (el 2026-10-17). *[ganancia]*

**Contradicción:** El commit 80b6abbf dice todo apagado y el registro fecha la integración el 2026-10-16; las transcripciones sitúan el cambio de defectos en ef5fb944 el 2026-10-17.

Fuentes: 3d7ca311 2026-10-16, 3d7ca311 2026-10-17, commit 80b6abbf

<a id="host_helper_lanes"></a>
#### `HOST_HELPER_LANES` — ON, def. on

Razonamiento: Hipótesis del paquete de contención: dar un núcleo físico propio (AboveNormal) al replay del ring y al front de GPU, fuera de los carriles del guest, reduce el offcpu del render y su hueco cpu→wall. El propio interruptor no ahorra core-ms (requiere ≥6 núcleos físicos); los -50 core-ms del brazo L venían sobre todo de dejar de girar (S: -41 core-ms, +0,9 FPS), y L sumó render CPU -4,3 ms. Se activó por defecto por criterio de ms en ef5fb944 aunque el commit lo declara opt-in. *[ganancia]*

**Contradicción:** El registro atribuye los -50 core-ms al interruptor; las transcripciones dicen que el interruptor no ahorra core-ms y que S (bloquear en lugar de girar, backoff) aporta ~-41.

Fuentes: 3d7ca311 2026-10-16, 3d7ca311 2026-10-17, commit 80b6abbf

<a id="idle_flush"></a>
#### `IDLE_FLUSH` — ON, def. 4

Razonamiento: En paneo la GPU no es el límite (parada 11-13 ms por frame de 35-37 ms): el hilo de render sigue grabando y el vaciado ocurre en cada lectura síncrona. Si la cola de GPU está vacía, enviar ya el lote abierto con ≥n draws/dispatches; es el mismo corte que pone un lote lleno, así que es exacto por construcción aunque sin modo verify de datos (se compararon capturas). n=4 baja la espera -1,6 ms; n=8+PASS pierde la ganancia porque cortar passes sube el trabajo sellado sin enviar a 7,6 ms. Se integró por exacto y bajar una espera concreta pese a FPS dentro del ruido. *[exacto-neutro]*

Fuentes: 3d7ca311 2026-10-31, commit 7032d618, commit be557443

<a id="layout_find_table"></a>
#### `LAYOUT_FIND_TABLE` — ON, def. on

Razonamiento: Dentro del paquete gta/perdraw (recortar el coste por draw en el hilo de render): BindingLayout.Find escaneaba y pasa a una tabla por layout. Cambio exacto, con su propio interruptor; se encendió por mecanismo (el paquete suma -2,06 ms/frame) tras verify con 0 discrepancias, sin A/B propio. *[ganancia]*

Fuentes: 3d7ca311 2026-10-25, commit c5813da2

<a id="mapped_size_keep"></a>
#### `MAPPED_SIZE_KEEP` — ON, def. 1

Razonamiento: Para recortar el hilo de render en paneo (BigWalk ~4,4 ms, Validate 9-11 MB/frame) se recuerdan los rangos totalmente mapeados entre flips: solo un unmap o Clear mueve MappingGeneration y puede volverlos ilegibles. Se enciende por mecanismo exacto (verify 0, ~0,45 ms/frame en paneo) aunque el A/B de FPS no distingue el cambio por ruido. La auditoría del 2026-11-07 lo da por seguro con workers (solo un contador estático racy). *[ganancia]*

Fuentes: 3d7ca311 2026-10-24, 3d7ca311 2026-10-25, 3d7ca311 2026-11-07, commit 2c0d8dba

<a id="mapped_size_single"></a>
#### `MAPPED_SIZE_SINGLE` — ON, def. on

Razonamiento: Parte del paquete perdraw: guardar las respuestas completas solo en el conjunto fully-mapped evita la doble consulta a diccionario (Dictionary TryGetValue 1,49 a 0 ms). Se enciende por mecanismo exacto con verify 0, junto al resto del paquete. *[ganancia]*

Fuentes: 3d7ca311 2026-10-25, commit c5813da2

<a id="overlay_apply"></a>
#### `OVERLAY_APPLY` — ON, def. on

Razonamiento: Diagnóstico del 10-10 de los blobs BLAS con ciclo (~3 de 4 sesiones) con la superposición de escrituras del frente: APPLY=0 aislaba si la superposición corrompía datos o exponía otra carrera. Dio 0/203 pero a 8,4 FPS y las repeticiones no fueron consistentes (0, 0 con giros, 20 con ciclo), así que no era el arreglo; la causa fue la caché [local] del planificador de tablas de store, nunca invalidada, y la superposición quedó apagada por defecto. *[otro]*

Fuentes: 3d7ca311 2026-10-10, commit 57c543a0

<a id="overlay_apply_cmd"></a>
#### `OVERLAY_APPLY_CMD` — ON, def. on

Razonamiento: Misma investigación de ciclos BLAS (10-10): se probó quitar el parche de la superposición en las lecturas de memoria de comandos del frente. Con =0 hubo 0 parches en ese sitio y aun así 64/204 blobs con ciclo, hipótesis refutada; la causa fue la caché de tablas de store. *[otro]*

Fuentes: 3d7ca311 2026-10-10, commit 57c543a0

<a id="packet_overrun"></a>
#### `PACKET_OVERRUN` — ON, def. on

Razonamiento: Crash FailFast en el concesionario de Simeon tras la 1.ª misión de Franklin, antes del autoguardado: un NOP ReleaseMemory de 8 dw en el offset 0x10 de un buffer de 20 dw. La consola ejecutaba ese buffer, así que el tamaño que guarda el emulador es el corto, no el paquete; ahora se lee el paquete entero de memoria y se vuelca el buffer. Activo por defecto como arreglo de estabilidad; si la cola no se puede leer sigue siendo fatal, y la causa de fondo del tamaño corto sigue pendiente. *[estabilidad]*

Fuentes: 3d7ca311 2026-11-06, commit 2d7b5716

<a id="prepare_stage_images"></a>
#### `PREPARE_STAGE_IMAGES` — ON, def. on

Razonamiento: P1 del plan de fondo: grabar el trabajo de imagen de un draw (barrera de data share, transiciones de textura, subidas de película) justo tras AcquireAttachments y antes del pipeline, porque en medio solo hay creación de pipeline y binds de vértices que las barreras no ordenan, así que la GPU ve lo mismo. El FPS quedó en ruido; se dejó por defecto porque habilita los caminos por lotes (P2, barreras acumuladas). Tras la fusión de upstream se portó al RenderRecorder. *[ganancia]*

Fuentes: 3d7ca311 2026-10-11, 3d7ca311 2026-11-03, commit 71b9d96e

<a id="queue_mask"></a>
#### `QUEUE_MASK` — ON, def. on

Razonamiento: El perfil mostró al frente ejecutando ProcessOne ~800k veces/s (reintentos de cabezas bloqueadas) escaneando las 57 colas con módulo: ~11 ms por fotograma. Se sustituyó por una máscara de 64 bits de colas no vacías, con _CHECK que compara con el escaneo completo (0 diferencias). Fue el arreglo del perfil con efecto claro en estática y paneo. *[ganancia]*

Fuentes: 3d7ca311 2026-10-13, commit d011d91f

<a id="record_fills"></a>
#### `RECORD_FILLS` — ON, def. on

Razonamiento: GTA V rellena registros con el shader 0x83D3; consumirlos como FillBuffer/DCC en vez de ejecutarlos baja las esperas del render. El 10-10 el reconocedor se dejó opt-in (sed a =="1") y más tarde figura como defecto activo con respaldo de relleno en CPU tras la fusión de upstream. En la fusión del 2026-11-04 una caída de fz34 se trazó a esta ruta (TryConsume en la cadena de publicación upstream), arreglada con IMAGE_PUBLICATION=0. *[otro]*

**Contradicción:** El 2026-10-10 un comando lo deja opt-in (=="1"); el 2026-11-03 se lo describe como default on. El registro dice ON sin mencionar el cambio.

Fuentes: 3d7ca311 2026-10-10, 3d7ca311 2026-11-03, 3d7ca311 2026-11-04

<a id="record_fill_image_clear"></a>
#### `RECORD_FILL_IMAGE_CLEAR` — ON, def. on

Razonamiento: GTA V limpia cada fotograma un stencil 4K (R8, 0x870000 bytes) con un relleno de registros; hacerlo en CPU (memset) dejaba la imagen sucia en CPU y la recargaba entera. Un relleno que cubre exactamente una imagen ahora se limpia en GPU: subidas de imagen 3,3 a 2,4 ms, capturas idénticas y las mismas 27 pruebas fallidas conocidas, así que se dejó encendido. *[ganancia]*

Fuentes: 3d7ca311 2026-10-11, commit 1abf4201

<a id="record_ring"></a>
#### `RECORD_RING` — ON, def. on

Razonamiento: Plan del grabador lineal (2026-10-12): la sonda VKCALL midió 6,93 ms/frame en llamadas al driver (Barrier 2,07, BindPipeline 1,12...), por encima del umbral de 4 ms, así que se dio GO con criterios de abandono (<2 ms o <+0,3 FPS). El render añade vkCmd* a un anillo y otro hilo los emite; ganó +1,33 FPS (3+3) y se encendió. Tras la fusión de upstream hubo que enrutar por VkRecord las llamadas directas nuevas. *[ganancia]*

Fuentes: 3d7ca311 2026-10-12, 3d7ca311 2026-11-03, commit 871f2c24, commit a7051375

<a id="record_ring_wake"></a>
#### `RECORD_RING_WAKE` — ON, def. 64 (SPIN_US 0)

Razonamiento: Con el anillo activo, el hilo de reproducción giraba hasta 1 ms antes de aparcar (20 µs dejaba ~410 aparcamientos/frame, 1 ms ~5). En la rama de contención (2026-10-16) se probó giro 0 y WAKE 256 para liberar CPU lógica; el 2026-10-31 WAKE 64 junto a DEFERRED_PROTECT_WORKERS=2 dio +0,6-0,8 FPS en paneo en dos A/B y se hizo defecto. *[ganancia]*

Fuentes: 3d7ca311 2026-10-12, 3d7ca311 2026-10-16, 3d7ca311 2026-10-31, commit 40c1ff23, commit 49844e25

<a id="register_copy_direct"></a>
#### `REGISTER_COPY_DIRECT` — ON, def. on

Razonamiento: Paquete del frente (junto a FRONT_SKIP_DEFERRING): el copiador de bancos de registros buscaba copiadores anidados y creaba objetos por reflexión en cada snapshot. Llamarlos directo y copiar arrays por spans baja el snapshot de 1,68 a 1,26 µs y el trabajo del frente 2,4-3,8 ms; exacto, el FPS no sale significativo, pero se pasó a defecto el 2026-10-13 junto a MATERIALIZE_REBASE. *[ganancia]*

Fuentes: 3d7ca311 2026-10-12, 3d7ca311 2026-10-13, commit 4a423350, commit 6cacd7d9

<a id="relay_guard"></a>
#### `RELAY_GUARD` — ON, def. on

Razonamiento: Evitar abrir el ámbito medido y la llamada de RunPendingCommands en cada draw/dispatch cuando el relay no tiene nada encolado. Sin efecto medible (QueueRelay 2,3 ms es trabajo real, no sondeo), pero es gratis y se mantiene; tras la fusión del 2026-11-03 se conservó tras el return de OnCommandThread de upstream, que tiene un guarda equivalente (2d269dc0). *[exacto-neutro]*

Fuentes: 3d7ca311 2026-10-12, 3d7ca311 2026-11-03, 3d7ca311 2026-11-06, commit 533a7651

<a id="relay_pump_in_waits"></a>
#### `RELAY_PUMP_IN_WAITS` — ON, def. on

Razonamiento: Una lectura de GTA al sondear la etiqueta CP (0x59C900000) cae en página GPU-dirty y la espera del completado del readback bloquea el render (p90 3,6 ms). Que el completado de la descarga corra también mientras el hilo de cola espera a la GPU solo cambia cuándo corre, no el orden, así que es exacto. Se apagó el 24-10 y figuró como descartado, pero gta/labelvis lo retomó; el 26-10 se encendió, mientras la alternativa de visibilidad exacta (3) elimina las cadenas de fallos pero baja de ~29 a ~22 FPS y queda apagada. *[ganancia]*

Fuentes: 3d7ca311 2026-10-24, 3d7ca311 2026-10-25, 3d7ca311 2026-10-26, commit 965282d5, commit de215e9f

<a id="rewind_patch_wake"></a>
#### `REWIND_PATCH_WAKE` — ON, def. 3

Razonamiento: Modo lento de GTA: sceAgcRewindPatchSetRewindState pone el bit válido pero no despierta al frente; si la GPU se drena solo el reintento de 100 ms lo desbloquea (paradas de 116-145 ms ~1,7/s). =1/=2 despiertan de forma exacta pero se quedan en ~27 FPS: el hilo de render es el camino crítico y la pérdida es un readback sincronizante por fotograma de la cola async compute; =3 (reintento a 2 ms con el frente bloqueado) lo resuelve y es defecto desde el 29-10. Un modo lento posterior (gcQb1) tuvo otra causa. *[modo-lento]*

Fuentes: 3d7ca311 2026-10-26, 3d7ca311 2026-10-29, 3d7ca311 2026-11-02, commit 31d57f9b, commit e4e7ee79

<a id="submit_thread"></a>
#### `SUBMIT_THREAD` — ON, def. on

Razonamiento: Plan 2026-10-12: sacar de la hebra de render la unión de subidas diferidas (DeferredUploadWait ~2 ms) y vkQueueSubmit2 a un hilo de envío al estilo RPCS3 (paso 1 barato, 2-3 ms esperados), antes de decidir el anillo completo. Ganó +0,47 FPS (3+3; QueueSubmit -0,92, DeferredUploadWait -2,00, con parte devuelta como GpuCompletionWait +0,7 y SubmitThreadWait +0,8) y se encendió. *[ganancia]*

Fuentes: 3d7ca311 2026-10-12, commit 95724e66, commit a14c9eed

<a id="target_memo"></a>
#### `TARGET_MEMO` — ON, def. 1

Razonamiento: Reutilizar la búsqueda de imagen de un target cuyos registros repiten los del draw anterior (ahorra ~1 ms de resolución pero se convertía en espera). Neutro al principio, pero -0,5 ms de mecanismo, exacto y sin empeorar el FPS más allá del ruido: el 2026-10-18 se activó por defecto. Tras la fusión de upstream el memo del executor no corría (hits=0) y se movió al RenderRecorder (2d87c93b); ImageLookupCore 0,80 a 0,47 ms. *[ganancia]*

Fuentes: 3d7ca311 2026-10-12, 3d7ca311 2026-10-17, 3d7ca311 2026-10-18, 3d7ca311 2026-11-06, commit 2d87c93b, commit cba327d5

<a id="target_memo_dcc"></a>
#### `TARGET_MEMO_DCC` — ON, def. on

Razonamiento: Los targets DCC (28 % de los draws) quedaban fuera del TARGET_MEMO y siempre buscaban imagen. Ahora reutilizan la imagen recordada mientras el host sigue sincronizando metadato y resolviendo el clear en cada draw. Verificado (180k+159k aciertos, 0 mismatches); la ganancia es pequeña (ImageLookupCore 0,47 a 0,25 ms), un paso del barrido de coste por draw hacia 30 FPS. *[ganancia]*

Fuentes: 3d7ca311 2026-11-05, 3d7ca311 2026-11-06, commit 3a5ff60d

<a id="usleep_short_pump"></a>
#### `USLEEP_SHORT_PUMP` — ON, def. on

Razonamiento: Los worker pools del guest llaman usleep(1) como retroceso de sondeo y cada llamada ejecutaba el Pump (cerrojo global de hilos guest, escanea todos). Saltarlo bajo 1 ms (el dispatcher de 1 ms sigue despertando esperas) entró al defecto dentro del paquete L de contención con las demás palancas (CPU del render -4,3 ms), no por separado: S solo no se integra aparte. *[ganancia]*

Fuentes: 3d7ca311 2026-10-16, commit 80b6abbf, commit db38c961

<a id="wait_own_first"></a>
#### `WAIT_OWN_FIRST` — ON, def. on

Razonamiento: Port de upstream #1103 (gta/waitown): una espera WAIT_REG_MEM satisfecha por el store propio de su cola no necesita leer memoria, lo que evita que el host termine trabajo y descargue páginas GPU-escritas. En GTA las lecturas de esperas bajan de 5.300/s a 430/s (~0,05 ms/fotograma), FPS sin cambio (24,53 vs 24,57); se integró como corrección. *[corrección]*

Fuentes: 3d7ca311 2026-10-22, commit 25f6adee

<a id="batch_barriers"></a>
#### `BATCH_BARRIERS` — OFF, def. off

Razonamiento: Tras la fusión con upstream varios defaults cambiaron (ventana BDA 2 MiB, ReBAR, BATCH_BARRIERS, SDL_POLL_MS, etc.). Por el principio de Daniel (donde upstream cambia el comportamiento, interruptor con defecto lo nuestro y upstream opt-in) se devolvió al camino de 3368666f. gz05 mostró que estos seis defaults no mueven FPS (27,29 vs 26,56) y gz10 con todos a lo nuestro dio 27,16: no eran la regresión. *[A/B-upstream]*

Daniel: Principio relayado por el coordinador: donde upstream cambió el comportamiento, interruptor SHARPEMU_* con defecto = lo nuestro (3368666f) y upstream opt-in; los tests de upstream lo activan con override AsyncLocal.

Fuentes: 3d7ca311 2026-11-04, commit 3fe66d3f

<a id="da_host_backing_cp_order"></a>
#### `DA_HOST_BACKING_CP_ORDER` — OFF, def. off

Razonamiento: Al investigar 0x260F se vio un hueco semántico real: WriteCommandData aplica la escritura del CP directamente en memoria guest al grabar cuando el rango no está registrado, y con las entradas de import la GPU podría leer ese valor antes de tiempo. El interruptor registra el rango para convertirla en copia en orden del stream. No se probó: en 260F las 13 palabras con 6.414 escrituras CP nunca cambian de valor, así que no había evidencia de que actuara. *[corrección]*

Fuentes: 3d7ca311 2026-10-18, commit ca37fb6f

<a id="front_end_core"></a>
#### `FRONT_END_CORE` — OFF, def. off

Razonamiento: Upstream reserva un segundo núcleo para su CommandThread con 12+ carriles aunque RENDER_PIPELINE=1 lo apaga: en GTA V con 16 carriles el guest quedaba con máscara 0xFF en vez de 0x3FF de 3368666f. Por el principio de Daniel se devolvió al comportamiento anterior con upstream opt-in. Con todo a lo nuestro seguía 26,56 frente a 30,10: la regresión estaba en otro sitio, y comparar con sondas encendidas no vale como paridad. *[A/B-upstream]*

Fuentes: 3d7ca311 2026-11-04, commit d4cec637

<a id="guest_priority"></a>
#### `GUEST_PRIORITY` — OFF, def. legacy

Razonamiento: Upstream mapea la banda alta de prioridades del guest a Normal y el resto a BelowNormal; nuestro legado usa Highest/Normal. Tras la fusión se devolvió al de 3368666f (upstream opt-in) por el principio de Daniel, junto con carriles y sondeo SDL. Con todo a lo nuestro seguía 26,56 vs 30,10, así que no era la causa de la regresión estática. *[A/B-upstream]*

Fuentes: 3d7ca311 2026-11-04, commit d4cec637

<a id="host_worker_priority"></a>
#### `HOST_WORKER_PRIORITY` — OFF, def. off

Razonamiento: Upstream sube los hilos worker del host a AboveNormal con su afinidad. Por el principio de Daniel se devolvió al comportamiento de 3368666f con upstream opt-in, sin A/B propio; con todo a lo nuestro seguía 26,56 vs 30,10, es decir, no era la regresión. *[A/B-upstream]*

Fuentes: 3d7ca311 2026-11-04, commit d4cec637

<a id="render_pipeline"></a>
#### `RENDER_PIPELINE` — OFF, def. off

Razonamiento: Idea de frente: el render hacía todo en serie (interpretar paquetes, materializar y preparar programas, ~26 ms de 74 ms). Un hilo de frente que interpreta por delante dio 13,8 a 20,1 FPS sin cambios de render y fue la ruta de medida desde el 2026-10-08. Tras la fusión de upstream (2026-11-03) coexiste/excluye con el CommandThread de upstream y hay un camino sin él (RENDER_PIPELINE sin definir) en que ya se hallaron fallos. *[ganancia]*

Fuentes: 3d7ca311 2026-10-08, 3d7ca311 2026-11-03, 3d7ca311 2026-11-06, commit 19a5961b

<a id="render_snapshot"></a>
#### `RENDER_SNAPSHOT` — OFF, def. off

Razonamiento: Base del hilo de frente (2026-10-08): el draw debe ejecutarse con los registros tal como estaban cuando corrió su paquete, no con los bancos vivos que el intérprete sigue escribiendo, así que corre sobre una copia (Clone ~30 objetos, 5 KB por llamada). Sirvió para validar que los programas preparados se usaban sin rechazos; después upstream trajo su Snapshot() copy-on-write y en la fusión se unió con nuestro CopyFrom. *[otro]*

Fuentes: 3d7ca311 2026-10-08, 3d7ca311 2026-11-03, commit 34a15bcf, commit a56b08f3

<a id="srt_compiler_priority"></a>
#### `SRT_COMPILER_PRIORITY` — OFF, def. below

Razonamiento: Diagnóstico (2026-10-13/14): saber si los evaluadores SRT llegan tarde y explican la rampa inicial de FPS. La sonda [PERF][SRT_COMPILE] mostró 323 planes compilados a los 90 s (1,6 s de compilación, espera máxima en cola 144 ms), todos hechos hacia los ~105 s y nada en cola después; subir la prioridad no cambiaría la rampa. El interruptor mismo no se midió. *[sin-A/B]*

Fuentes: 3d7ca311 2026-10-13, 3d7ca311 2026-10-14

<a id="bda_read_only_scopes"></a>
#### `BDA_READ_ONLY_SCOPES` — Descartado, def. off

Razonamiento: Portado de xnetcat 2aa04103 con una prueba más estricta: una etapa que solo lee por device address (loads/BVH, sin rangos escritos, sin llamadas) no cuenta como escritura de memoria, así no fuerza el fin de un ámbito de render podado ni anota escritura al rastreador de barreras. Queda apagado porque quitar una barrera no se puede verificar sin partida y fp2 no dio nada visible; reproducir la mejora declarada de DescriptorPreparation (47→34 ms) del memo por referencia tampoco funcionó. *[sin-A/B]*

Fuentes: 3d7ca311 2026-11-06, 3d7ca311 2026-11-07, commit af0ab595

<a id="defer_draw_transitions"></a>
#### `DEFER_DRAW_TRANSITIONS` — Descartado, def. on

Razonamiento: P2 del plan de transiciones diferidas: agrupar las transiciones de imagen sueltas de cada draw en una sola vkCmdPipelineBarrier2 antes del draw (mismo hueco entre draws, orden preservado; por defecto con máscaras unidas, exact y split como variantes). Las llamadas a barreras solo bajaron un 4 % (~2100 son de buffer/memoria) y el FPS fue neutro; no se integra, y P3 no aplica sin P4. *[exacto-neutro]*

**Contradicción:** El registro lo da con defecto on, pero esa rama (p2-transiciones) no se integró y el interruptor no existe en la rama actual (2026-11-03).

Fuentes: 3d7ca311 2026-10-12, 3d7ca311 2026-11-03, commit 5c25d662

<a id="defer_patched"></a>
#### `DEFER_PATCHED` — Descartado, def. off

Razonamiento: Los árboles BVH con ciclos (~3 de cada 4 ejecuciones) se atribuyeron a la superposición de escrituras pendientes del front. Se probó diferir al fondo las preparaciones que leen bytes parcheados: no arregló los ciclos (58/222 y 58/225 blobs) y dejó 3,3 FPS persistentes, probable estado roto de alguna caché al desenrollar la excepción. La causa real era la superposición, resuelta con FRONT_WRITE_OVERLAY=3; la bitácora avisa de no reutilizarlo sin entender el fallo, y el precedente pesa en cualquier idea de abandonar y reiniciar una preparación. *[corrección]*

Fuentes: 3d7ca311 2026-10-10, 3d7ca311 2026-10-18, commit 57c543a0

<a id="depth_resolve_memo"></a>
#### `DEPTH_RESOLVE_MEMO` — Descartado, def. 0

Razonamiento: DepthTargetResolver.Resolve reconstruía la petición de depth y el estado de stencil en cada draw; se memorizó el último resultado mientras no cambien los registros, como ya hacía ColorTargetResolver. Es exacto (0 discrepancias en ~300.000 comprobaciones) pero no ahorra nada: los ~0,35 ms atribuidos a la resolución de depth eran el cronómetro de la sonda y la comparación de la clave. El resolver ya era barato. *[coste-sonda]*

Fuentes: 3d7ca311 2026-10-18, commit c418dcb5

<a id="front_poll_blocked"></a>
#### `FRONT_POLL_BLOCKED` — Descartado, def. off

Razonamiento: Hipótesis: con todas las cabezas bloqueadas, reintentarlas cada 1 ms (como sondea el CP) acortaría la espera tras aterrizar la escritura de la etiqueta. No cambió nada (la espera seguía en 11-13 ms) y cada slice reintentado encola un inicio de envío y un flush: ítems por frame de ~2.600 a ~18.000 y FPS de paneo 15,0. Lo que satisface la espera es otra cosa (hipótesis sin verificar: la finalización del tick por la GPU y la escritura posterior del guest). *[coste-sonda]*

Fuentes: 3d7ca311 2026-10-12, commit fd726dc1

<a id="front_prep_worker"></a>
#### `FRONT_PREP_WORKER` — Descartado, def. off

Razonamiento: Objetivo: sacar la preparación de programas del front (23-30 ms/frame) a un worker en orden; exacto porque el render re-prepara si no llega a tiempo o caduca. Activo dio paneo 26,39→28,04, pero dos sesiones de paneo perdieron el dispositivo (DeviceLost) y el verify halló un dispatch (0x6A53) con etapa de vértices distinta; tras corregir una carrera de la caché de tablas y añadir STORE_TABLE_APPLIED_CHECK dio 0 mismatches en 8,8 M comparaciones. Se encendió solo para draws (paneo +1,48, estática no peor) y después se apagó de nuevo porque costaba en estática (27,83→25,51) y aumentaba las sesiones lentas. *[modo-lento]*

**Contradicción:** El A/B del 2026-10-23 daba estática +0,89 con solo draws (commit 42d29e8f) y el mismo día 02768616 midió 27,83→25,51 con el worker activo; las cifras de estática son contradictorias entre tandas.

Fuentes: 3d7ca311 2026-10-23, commit 02768616, commit 42d29e8f, commit 57cc8277, commit d35f6935

<a id="front_prep_worker_compute"></a>
#### `FRONT_PREP_WORKER_COMPUTE` — Descartado, def. off

Razonamiento: Los mismatches del verify que precedieron al DeviceLost fueron siempre en dispatches (0x6A53), nunca en draws, y el worker con dispatches costaba en estática (-0,68 FPS, 4 de 4 parejas) mientras que solo con draws daba +0,89/+1,48 en paneo. El 42d29e8f invirtió el defecto: el 6b9f6362 original tenía los dispatches en el worker y =0 los quitaba; desde 42d29e8f están en el front salvo =1. Hipótesis sin medir: el render espera preparaciones de compute en curso, más caras. *[corrección]*

**Contradicción:** La incoherencia del registro se explica por el commit 42d29e8f, que cambió el defecto de 6b9f6362 (=0 quitaba dispatches) a apagado (=1 los añade).

Fuentes: 3d7ca311 2026-10-23, commit 42d29e8f, commit 6b9f6362

<a id="front_speculate"></a>
#### `FRONT_SPECULATE` — Descartado, def. 0

Razonamiento: En paneo el render preparaba ~145 dispatches de cómputo por frame (192 en los perdidos), 3,2 ms (5,8) de DispatchProgram, porque el front los abandonaba al primer dato escrito por GPU. El front los prepara con los bytes actuales, los registra y el render los repite y valida. Ahorra 1,6 ms en frames normales, pero en los fotogramas perdidos más espera de readback anula el ahorro (ReadbackRender 2,87→6,17 ms): el tiempo de preparación ahorrado se convierte en esperas de GPU. *[adelanta-cuello]*

Fuentes: 3d7ca311 2026-11-03, commit 96d8a61c, commit 9ebacdd6

<a id="front_wake"></a>
#### `FRONT_WAKE` — Descartado, def. off

Razonamiento: La tarea partía de un perfil desactualizado de ~800 k reintentos de ProcessOne/s y ~21 ms/frame de sondeo del front. La máscara de colas (2527fcd5) ya había bajado los slices de 4,1 M a 8,4 k por ventana y la CPU del front de 897 a 477 ms/s, así que no quedaba sondeo que ahorrar. Aparcar cabezas bloqueadas y despertarlas al aplicar una escritura sí funciona (bloqueos ÷3) pero da +0,09 estática / +0,50 paneo, sin significación; quedó en la lista de exactos-neutros a repetir cuando suba el suelo del bucle del juego (~43,5 ms). *[exacto-neutro]*

Fuentes: 3d7ca311 2026-10-13, 3d7ca311 2026-10-14, commit b78382ab

<a id="record_ring_hybrid"></a>
#### `RECORD_RING_HYBRID` — Descartado, def. off (ACTIVE_US 200)

Razonamiento: Hipótesis: el hilo de reproducción girando roba ciclos al hyper-thread del productor. Se probó girar solo mientras el productor añade (últimos 200 µs): el uso del hilo bajó de 89 % a 62-78 % pero el FPS no cambió (+0,08 ± 0,15), hipótesis refutada; queda opt-in. *[exacto-neutro]*

Fuentes: 3d7ca311 2026-10-13, commit b78382ab

<a id="record_ring_merge"></a>
#### `RECORD_RING_MERGE` — Descartado, def. off

Razonamiento: Se probó fundir barreras adyacentes en la reproducción del anillo para bajar las 2655 barreras/fotograma. Casi todas usan AllCommands y no son fusionables: 0 fusiones, queda apagada. *[otro]*

Fuentes: 3d7ca311 2026-10-12

<a id="retry_on_guest_write"></a>
#### `RETRY_ON_GUEST_WRITE` — Descartado, def. off

Razonamiento: Las colas bloqueadas solo reintentaban al completar un tick, en presentación o a los 100 ms; se probó reintentarlas al aplicar el render una escritura del CP (rt400: ocio del render 16 a 12-13 ms). Las esperas bajan ~1,6 ms pero la resolución sube lo mismo y el FPS no se mueve (15,60 vs 15,67; paneo +0,5 con mucho ruido): el fotograma lo marca la cadena guest-frente-back-GPU-etiqueta. Regla aprendida: solo recortar trabajo del back paga, no recortar esperas. *[adelanta-cuello]*

Fuentes: 3d7ca311 2026-10-10, 3d7ca311 2026-10-11, 3d7ca311 2026-10-12, commit 6a89fd55

<a id="runahead_ticks"></a>
#### `RUNAHEAD_TICKS` — Descartado, def. 0 (GUEST on, MAX_US 4000)

Razonamiento: Hipótesis: limitar cuánto se adelanta el render a la GPU mientras un hilo del juego espera un readback reduciría las esperas del juego. Con n=4 las esperas bajan de 31 a 20 ms pero el render queda retenido el 26 % del tiempo y el FPS cae a 25,23 frente a ~28,8; se deja como interruptor negativo, con test. *[adelanta-cuello]*

Fuentes: 3d7ca311 2026-11-01, 3d7ca311 2026-11-02, commit 36b59aed

<a id="stamp_on_change"></a>
#### `STAMP_ON_CHANGE` — Descartado, def. 0

Razonamiento: Los registros reescritos con el mismo valor movían el sello e invalidaban los memos por sello. El 39 % de las escrituras dejan de mover el sello, pero los registros de programa cambian en el 70 %: layout_resolves -5 % y ninguna fase >= 0,1 ms. Verificado exacto (92,9k layouts, 0 mismatches) pero neutro (~+0,2 FPS, ruido). *[exacto-neutro]*

**Contradicción:** Un informe del agente (2026-11-05/06) proponía STAMP_ON_CHANGE=1 por defecto condicionado a dc19/dc17<dc18; el registro lo da por defecto 0 y Descartado. El motivo final solo consta como neutro.

Fuentes: 3d7ca311 2026-11-06, commit 8b0449ec

<a id="submit_batch"></a>
#### `SUBMIT_BATCH` — Descartado, def. 0

Razonamiento: Hipótesis: el hilo de envío junta varios sellados listos en un solo vkQueueSubmit2 y ahorra llamadas. En juego da 1,09-1,10 búferes por llamada, apenas junta: exacto pero nulo (paneo 29,05 vs 28,93, ruido) y se descarta; gta/pan2 concluyó que no hay palanca exacta de >=1,2 ms rápida. *[exacto-neutro]*

Fuentes: 3d7ca311 2026-11-01, 3d7ca311 2026-11-02, commit 35c88e4c

<a id="suspend_points_in_flight"></a>
#### `SUSPEND_POINTS_IN_FLIGHT` — Descartado, def. 1

Razonamiento: Variable de upstream (6283b7a8, defecto 1); se probó dejar que el guest corra 2 o 3 fronteras por delante para ver si el render esperaba al juego. 10,05-10,26 FPS, sin cambio: el juego no espera en las fronteras. Solo se añadió el parseo numérico [local]. *[otro]*

Fuentes: 3d7ca311 2026-10-10, commit 6283b7a8, commit 6a89fd55

<a id="timeline_poll_us"></a>
#### `TIMELINE_POLL_US` — Descartado, def. 0

Razonamiento: Hipótesis: unas ~140 lecturas del timeline por fotograma (~2 µs cada una, vkGetSemaphoreCounterValue) se pueden reducir si otro hilo leyó hace < n µs. Con 50 µs se salta ~50 % (~0,15 ms) pero las dos sesiones salen peores (27,44 y 26,22), mecanismo sin identificar. La variante exacta de leer el timeline una vez por recolección (a77d3f38) sí se integró. *[exacto-neutro]*

Fuentes: 3d7ca311 2026-11-01, 3d7ca311 2026-11-06, commit a77d3f38, commit db2b0e86

<a id="timer_resolution"></a>
#### `TIMER_RESOLUTION` — Descartado, def. on

Razonamiento: timeBeginPeriod(1) lo pedía el vblank pump, eliminado en upstream #216; desde entonces las esperas de 1 ms del frente y del render se redondeaban al tick por defecto. A/B 2+2 estático: 24,36 vs 24,26 y latencia del frente p50 4,2 vs 4,1 ms, neutro: no se integra (solo queda encendido en su rama gta/timerres). *[exacto-neutro]*

Fuentes: 3d7ca311 2026-10-22, commit 186f5c24

Sin razonamiento encontrado (1): `FRONT_SYNCED_READS`.

### slices/paralelo (14)

<a id="render_workers"></a>
#### `RENDER_WORKERS` — ON, def. auto

Razonamiento: Defecto de upstream (workers de grabación de slices). Al fusionar origin/main se puso a lo nuestro junto a otros defectos upstream (fz24: RESOLVE/RENDER_WORKERS=0 etc.) y dio 4,80 FPS, pero la regresión real era TABLE_BLOCKS; con RENDER_PIPELINE=1 quedan inertes. Después sí se usan como N de los workers de FRONT_SLICES (parres), donde 6 workers empeoraron (151,8 vs 142,1 ms) y 4 no bajaron el coste por elemento. *[A/B-upstream]*

**Contradicción:** El registro dice que con RENDER_PIPELINE=1 no se usan; en gta/parres FRONT_SLICES=1 sí los usa como workers de grabación de slices.

Fuentes: 3d7ca311 2026-11-04, 3d7ca311 2026-11-07

<a id="resolve_workers"></a>
#### `RESOLVE_WORKERS` — ON, def. clamp(CPU/4,1,6)

Razonamiento: Defecto de upstream (ResolvePool que resuelve por delante y valida en la unión). Al fusionar upstream se probó ponerlo a 0 con otros defectos y la regresión resultó ser TABLE_BLOCKS; gz05 mostró que no afecta a los FPS e inerte con RENDER_PIPELINE=1. En gta/parres, FRONT_RESOLVE_POOL con 4 workers no bajó draw_resolve (4,8 µs/draw) y con RESOLVE_WORKERS=0 la fontanería costaba ~+55 ms/fotograma, así que el pool quedó apagado. *[A/B-upstream]*

**Contradicción:** El registro dice «sin medir»; sí hubo medidas: gz05 (sin efecto en FPS) y las ramas y2a/y2b/y2c de FRONT_RESOLVE_POOL.

Fuentes: 3d7ca311 2026-11-04, 3d7ca311 2026-11-07

<a id="front_slices"></a>
#### `FRONT_SLICES` — OFF, def. off

Razonamiento: Paso 3a de la ruta (a) de Daniel: los render workers de upstream graban los draws del frente por segmentos y se unen en orden, para bajar la CPU del render de GTA hacia 20 FPS con puerta de 90 ms. Las estimaciones de los agentes salieron ~2x optimistas: los workers cuestan ~31 µs/elemento frente a ~19,7 inline y el render queda en 128-139 ms; MIN pasa a 32 porque las rachas cortas solo añadían espera. Sigue apagado, línea en curso. *[otro]*

Daniel: Eligió la ruta (a): que nuestro frente alimente los pools de upstream (relatado en el mensaje del coordinador al agente, 2026-11-06).

Fuentes: 3d7ca311 2026-11-06, 3d7ca311 2026-11-07, commit ad34ee28, commit d58413fa

<a id="front_slice_defer"></a>
#### `FRONT_SLICE_DEFER` — OFF, def. off

Razonamiento: Paso 3c: los posts sin espera (Flush, RecordEndOfPipe, TriggerInterrupt...) pasan a unidades de render para que los slices terminados se unan pronto. Medido: sbtd 139 ms vs sbt 128 ms (6,43 vs 7,98 FPS); no ayuda porque los posts casi no aparecen y el 93 % de los cortes es WriteGuest. Sigue apagado. *[otro]*

Fuentes: 3d7ca311 2026-11-07, commit b5ebfd3e

<a id="front_slice_dispatches"></a>
#### `FRONT_SLICE_DISPATCHES` — OFF, def. off

Razonamiento: Paso 3b: que los workers graben también los dispatches directos del frente. Resultado: sbx1 142,1 ms y 6,49 FPS frente a sd1 122,2 ms y 7,53; los indirectos thread-unit vuelven a serie (530-700/fotograma) y la ganancia es nula; con SLICE_THREAD_INDIRECT baja a 128 ms, aún sobre la puerta de 90. Un agente concluyó que tres rondas de slices no habían movido el µs/elemento. *[otro]*

Fuentes: 3d7ca311 2026-11-07, commit da5e3827

<a id="front_slice_writes"></a>
#### `FRONT_SLICE_WRITES` — OFF, def. off

Razonamiento: Último intento de la línea de slices (3e): el 93 % de los cortes de slice (~235/frame) son WriteGuest del intérprete (etiquetas RELEASE_MEM/EOP, volcado de oclusión), así que se convierten en unidades de render ordenadas en vez de cortar. Esperado: cortes de ~258 a ~25 y join_wait 32 a 5-10 ms, pero join 12 a 18-22 ms; el agente estima el render en 105-115 ms, probablemente sin cruzar la puerta de 90 ms. Aún sin medir en partida. *[sin-A/B]*

Fuentes: 3d7ca311 2026-11-07, commit f5662a50

<a id="slice_dcc_native"></a>
#### `SLICE_DCC_NATIVE` — OFF, def. off

Razonamiento: Los workers de slice rechazaban la materialización DCC de una petición con clear nativo, cortando el slice. La relajación exacta la acepta como no-op, igual que MaterializeDccClear; se añadió lookup_detail para contar la causa y el brazo SDN solo tenía sentido si aparecía dcc_metadata_native. *[corrección]*

Fuentes: 3d7ca311 2026-11-07, commit d3e2376d

<a id="slice_stencil_plane"></a>
#### `SLICE_STENCIL_PLANE` — OFF, def. off

Razonamiento: Los workers rechazaban todo depth target con stencil cuya imagen tiene más capas que la vista (~200/fotograma) porque comparaban el plano de stencil con el span de la petición, mientras la adquisición guarda el plano de todas las capas: la comparación nunca podía coincidir. El arreglo es exacto y quita ~200 cortes por fotograma; en la sesión sd1 (slices + este interruptor) el render bajó a 122,2 ms vs 136,0 de base, pero el coste por elemento no se movió (24,4 vs 23,8 µs). *[corrección]*

**Contradicción:** El registro dice «sin medir», pero sd1 (SLICES + STENCIL_PLANE) dio render 122,2 ms vs 136,0 de la base sin slices.

Fuentes: 3d7ca311 2026-11-07, commit d3e2376d

<a id="slice_thread_indirect"></a>
#### `SLICE_THREAD_INDIRECT` — OFF, def. off

Razonamiento: Los 530-700 serials por fotograma «indirect_thread_dispatch» no eran DispatchIndirectDeferred sino FrontDispatch con recuentos resueltos en GPU (SHARPEMU_GPU_THREAD_INDIRECT) que el worker devolvía al render, terminando su segmento. Con el interruptor los worker los graban: serials indirectos 707→0 y segmentos 848→112, pero el render solo bajó de 133 a 128 ms (puerta de 90 ms fallida) porque quedaban los ~235 cortes por WriteGuest. *[adelanta-cuello]*

**Contradicción:** El registro dice «sin medir»; sbt lo midió: 128 ms vs 133 de sbb.

Fuentes: 3d7ca311 2026-11-07, commit 66e8d8bc

<a id="front_resolve_pool"></a>
#### `FRONT_RESOLVE_POOL` — Descartado, def. off

Razonamiento: Paso 2 de la ruta (a) elegida por Daniel (que el frente alimente los pools de upstream): el render cede los draws encolados por el frente (hasta AHEAD=8) al ResolvePool de upstream. Medido: join_wait 6,9 ms/frame y resolve 4,8 µs/draw sin bajar; al rehacerlo (c0909068) pb1 dio 4,3 vs 3,43 µs/draw, pérdida neta ~0,9 µs/draw. Se queda apagado. *[otro]*

Daniel: Eligió la ruta (a): que nuestro frente alimente los pools de upstream (relatado en el mensaje del coordinador al agente, 2026-11-06).

Fuentes: 3d7ca311 2026-11-06, 3d7ca311 2026-11-07, commit be09d9dc, commit c0909068

<a id="record_batches"></a>
#### `RECORD_BATCHES` — Descartado, def. off

Razonamiento: Spike para grabar cada scope de rendering en un command buffer secundario como base para grabar en otros hilos; funciona y el render es idéntico, pero cuesta ~20 µs por lote (19,0 vs 20,6 FPS con todos los scopes). El hilo grabador posterior (pasos 3 y 4) salió negativo y se revirtió: grabar un draw son ~3 µs y la entrega se come los 2,5 ms que ahorra el hilo; solo pagaría llevándose la preparación pesada (transiciones diferidas), semanas de trabajo. *[sin-A/B]*

Fuentes: 3d7ca311 2026-10-08, commit aa213601

<a id="spec_resolve"></a>
#### `SPEC_RESOLVE` — Descartado, def. off

Razonamiento: Spike de medición de 2-3 días antes de gastar semanas: un hilo resuelve en sombra y de solo lectura las búsquedas de imagen de los K draws siguientes, para ver si la resolución especulativa es viable (los memos estaban agotados: 77 % de bindings distintos dentro del fotograma). Acertó 99,6 % pero la sombra cuesta más (3,08 vs 2,55-2,70 µs/draw) y añade +0,5 ms de cerrojo, así que se abandonó. *[otro]*

Fuentes: 3d7ca311 2026-10-11, commit 8f6db85a, commit b3d2a1d7

<a id="parrec_probe"></a>
#### `PARREC_PROBE` — Sonda, def. off

Razonamiento: Sonda para dimensionar cuánto trabajo del render (emisión de draws, dispatches, ámbitos, trabajo puro) podría emitir otro hilo en una grabación paralela. En fotogramas perdidos lo recordable son ~5 ms brutos del hueco de +15 ms (~8,6 ms es resolución de estado compartido de contenido nuevo), así que se descartó la grabación en paralelo. *[otro]*

Fuentes: 3d7ca311 2026-11-03, commit 14a516a2, commit d1fb7174

<a id="parres_probe"></a>
#### `PARRES_PROBE` — Sonda, def. off

Razonamiento: Con el hilo de render limitado por CPU (~176 ms/fotograma, ~4.600 draws), la sonda mide qué fracción del trabajo de draws podrían tomar el pool de resolve y los slices antes de invertir semanas. El paso 2 (FRONT_RESOLVE_POOL) no dio ganancia neta y quedó apagado; la sonda sirvió de puerta para decidir los slices del paso 3. *[otro]*

Fuentes: 3d7ca311 2026-11-06, 3d7ca311 2026-11-07, commit ddae9010

### presentación (6)

<a id="async_present"></a>
#### `ASYNC_PRESENT` — ON, def. 1

Razonamiento: Hipótesis: vkQueuePresentKHR cuesta tiempo en el hilo de render. La primera versión dejó pasar flips más rápido que el ritmo (hasta 32 FPS en paneo) porque tomaba la siguiente presentación con el present aún en vuelo; se arregló terminándolo antes (360200cd) y luego grabando comandos en cola en vez de esperar (f489dec3). Por defecto con −0,4 ms de render y −24 % de huecos; el 3-nov se vio que en ráfagas el render aún bloquea 4,3 ms en TryFinish (Monitor.Wait). *[ganancia]*

Fuentes: 3d7ca311 2026-11-03, commit 360200cd, commit bc90a230, commit f489dec3

<a id="render_work_budget_ms"></a>
#### `RENDER_WORK_BUDGET_MS` — ON, def. 4

Razonamiento: Upstream deja el vaciado de la cola del render sin tope (0) fuera de macOS, y CanPresentFlip solo se comprueba tras vaciarla: los flips listos perdían su ranura de 33,3 ms. Un tope de 4 ms los recoge a tiempo; esto explicaba por qué acelerar cualquier eslabón bajaba FPS. Tope de 2 ms dio un corte (22,15 FPS) y 8 ms rinde igual. *[corrección]*

Fuentes: 3d7ca311 2026-10-22, 3d7ca311 2026-10-23, commit 5f6efe5e

<a id="sdl_auto_joysticks"></a>
#### `SDL_AUTO_JOYSTICKS` — ON, def. on

Razonamiento: El muestreador veía 2,1-3,5 ms/frame en el hilo de render bajo SDL_PollEvent→SDL_UpdateJoysticks (el nombre SDL_wcstoull estaba mal resuelto: es sondeo de joysticks y sensores). Hipótesis: apagar el auto-update y sondear menos quita 0,8-1,3 ms/frame; en estática lo quita, pero el FPS no mejora (paneo 20,76→19,57, dentro de ±1) y el mando físico no se pudo probar, así que no se activa por defecto. *[exacto-neutro]*

**Contradicción:** El informe del agente (2026-10-17) dice que los interruptores no quitan nada en paneo y que el FPS de paneo bajó de 20,76 a 19,57; el registro cita paneo +0,52 (n=5) del brazo J de otra tanda.

Fuentes: 3d7ca311 2026-10-14, 3d7ca311 2026-10-17, commit e0a03f78

<a id="sdl_poll_ms"></a>
#### `SDL_POLL_MS` — OFF, def. 0

Razonamiento: Mismo origen que SDL_AUTO_JOYSTICKS (R1 del plan estructural: limitar el sondeo de SDL a una vez por N ms). Tras la fusión con upstream, 0 pasó a significar 4 ms, así que las sesiones previas con SDL_POLL_MS=0 nunca probaron lo nuestro; se restauró 0 = cada vuelta como 3368666f, con 4 como opt-in de upstream. Con todo a lo nuestro seguía en 26,56 frente a 30,10: la regresión estaba en otro sitio. *[A/B-upstream]*

Fuentes: 3d7ca311 2026-10-14, 3d7ca311 2026-10-17, commit d4cec637

<a id="present_prompt"></a>
#### `PRESENT_PROMPT` — Descartado, def. off

Razonamiento: El render solo presentaba tras vaciar toda la cola de trabajo pendiente (flip→present 77,6 ms con FlipDone del front 13,7 ms). Presentar en cuanto el fotograma más viejo está listo es exacto y deja la espera en 0, pero el front pasa a esperar al juego (Rewind y sin trabajo, ~24 ms/frame): la espera no estaba en la ruta crítica. El coordinador acordó dejarlo apagado; el reloj de flip no es más estricto que una PS5. *[adelanta-cuello]*

Fuentes: 3d7ca311 2026-10-17, commit 59dea638

<a id="present_yield"></a>
#### `PRESENT_YIELD` — Descartado, def. off

Razonamiento: Mismo problema de recogida tardía de flips: ceder entre elementos de la cola cuando hay un flip presentable (=2: solo si hay otro detrás, para dejar siempre un frame en cola). Recoge el flip en 1-2 ms y gana en paneo, pero 2 de 4 sesiones estáticas tuvieron cortes (p99 105-122 ms); hipótesis sin probar: el juego se engancha a una fase lenta de la rejilla de 30 Hz al quedarse la cola vacía. Se prefirió el tope de 4 ms por estabilidad. *[estabilidad]*

Fuentes: 3d7ca311 2026-10-22, 3d7ca311 2026-10-23

### memoria/CPU/arranque (27)

<a id="child_aslr"></a>
#### `CHILD_ASLR` — ON, def. low

Razonamiento: El juego moría a veces al arrancar ("Fixed mapping cannot claim its host range"): las pilas de los hilos de arranque del CLR caían al azar dentro de la ventana de memoria fija del guest antes de preservarla (33 regiones ajenas en 14 de 15 arranques con ASLR alto). Con ASLR bajo-up/alta entropía apagado van muy por debajo: 0 regiones ajenas en 15/15 y 30 arranques sin fallo; se puso por defecto por estabilidad, con FPS neutros (+0,06 estático, +0,10 paneo; el -1,1 previo de paneo era ruido de n=2). *[estabilidad]*

**Contradicción:** El registro cita paneo 17,88 vs 19,01 y fecha 2026-10-18; la medición final del A/B (2+2, 17-10) fue neutra (24,21 vs 24,11) y el -1,1 previo se atribuyó a ruido; el commit bc02c288 es del 17-10.

Fuentes: 3d7ca311 2026-10-17, commit 5a6ea6b1, commit bc02c288

<a id="cpu_dirty_skip"></a>
#### `CPU_DIRTY_SKIP` — ON, def. on

Razonamiento: Una escritura de CPU sobre páginas ya CPU-sucias no cambia nada (una página nunca es CPU y GPU sucia a la vez), así que se responde sin el cerrojo de región; idea de upstream #1082, implementación propia. Solo hay A/B del paquete de 7 recortes (+0,49 FPS), no aislado; se anotó como primer candidato a bisecar si una sesión «on» se colgaba. En la fusión de nov. se endureció: el salto exige páginas CPU-sucias y escribibles. *[sin-A/B]*

**Contradicción:** El registro dice «sin A/B de FPS»: existe el A/B del paquete de recortes (16,23 vs 15,74, +0,49), pero no aislado. Tras la fusión del 03-11 el interruptor se sigue leyendo y se endureció (CPU-sucia y escribible).

Fuentes: 3d7ca311 2026-10-12, 3d7ca311 2026-11-03, commit 24ebb838

<a id="eboot_guard"></a>
#### `EBOOT_GUARD` — ON, def. on

Razonamiento: Consecuencia del ASLR bajo: con first-fit, el alias de respaldo de 16,4 GiB (MapViewOfFile sin dirección) caía justo tras el rango de 16 GiB del GC y encima de la base del eboot (0x800000000). Se reserva [0x8_0000_0000, ventana) antes de crear PhysicalVirtualMemory y se libera justo antes de cargar la imagen principal; la nota previa de holgura era errónea. *[estabilidad]*

Fuentes: 3d7ca311 2026-10-15, 3d7ca311 2026-10-16, commit ceb1e411

<a id="gc_gen0_mb"></a>
#### `GC_GEN0_MB` — ON, def. 64

Razonamiento: GTA asigna 230-380 MB/s: con el presupuesto por defecto eran ~20 gen0/s y 40-47 ms de pausas por segundo; con 64 MB, 2-2,6 gen0/s y 26-32 ms (pausas de ~10 ms). Aislado no se distinguía del ruido (estático -0,34±0,28, paneo +0,15±0,47) pero en el conjunto de arreglos era el mejor brazo. Con MATERIALIZE_POH el suelo de 64 duplicaba las colecciones, de ahí 192 MB. *[sin-A/B]*

**Contradicción:** El 17-10, ya con 64 MB, aún había pausas de ~41 ms (23 gen0 + 9 gen1 en 12 s, 4,5 ms/frame), lo que llevó a GC_SERVER.

Fuentes: 3d7ca311 2026-10-13, 3d7ca311 2026-11-02, commit 0711b30c, commit 2527fcd5

<a id="gc_server"></a>
#### `GC_SERVER` — ON, def. on

Razonamiento: Con gen0=64 MB seguía habiendo 4,5 ms/frame de pausas del GC con el render parado. La idea (propuesta por Daniel según el asistente: «tu idea promete») fue el GC clásico de servidor (clrgc.dll, 2 heaps, no concurrente): 3 veces menos colecciones, pausa ~655 → ~118 ms por 20 s, estático +0,42, paneo neutro, memoria igual. Se encendió tras descartar que un segfault en 1 de 15 arranques fuese del nuevo GC (colisión con direcciones fijas). *[ganancia]*

Fuentes: 3d7ca311 2026-10-17, commit e9c5807e

<a id="hle_span_writes"></a>
#### `HLE_SPAN_WRITES` — ON, def. on

Razonamiento: Seis hilos Subrender se disputaban el ReaderWriterLockSlim de PhysicalVirtualMemory al escribir cada dword de los paquetes Agc (4,5–8 ms/frame de spin, Rewind del front 24–29 ms). Idea de upstream #1082 (Berk): una escritura por paquete. Con los tres interruptores M el Subrender bajó de ~30 a ~10 ms y el Rewind a ~1 ms; se encendieron por defecto el 17-10 (arreglando 10 tests de Agc). *[ganancia]*

**Contradicción:** El primer A/B del 15-10 (arm M) dio estática 20,23 vs 20,90 (peor) porque el juego enviaba antes y las esperas de GPU se duplicaban; el +3-4 FPS del registro es de integrar con las esperas arregladas el 17-10.

Fuentes: 3d7ca311 2026-10-15, 3d7ca311 2026-10-17, commit c04a95e0

<a id="lazy_back_reads"></a>
#### `LAZY_BACK_READS` — ON, def. on

Razonamiento: Los lectores de palabras del guest (TryReadGuestWord) asignaban una clausura para el hilo de render en cada llamada (~41 MB/s). Las lambdas pasan a helpers y solo se crean cuando la lectura va de verdad al render (=0 reserva un objeto del tamaño antiguo para medir). Nunca se midió aislado: forma parte del paquete que dio estática 18,93→19,78 y paneo 16,19→17,21. *[ganancia]*

Fuentes: 3d7ca311 2026-10-13, commit 04380721

<a id="ledger_snapshot"></a>
#### `LEDGER_SNAPSHOT` — ON, def. on

Razonamiento: La traza de CPU puso GuestPermissionLedger.Lookup (cerrojo global por página tocada, compartido con el manejador de faltas) como la mayor fuente de esperas de cerrojo en el render (~560 ms en total). Las lecturas pasan a una instantánea inmutable que solo se republica en map/mprotect (raros); ganó ~0,3 FPS. Tras la fusión del 03-11 se adoptó el PermissionRangeTree de upstream y =0 ahora toma el cerrojo sobre ese árbol. *[ganancia]*

**Contradicción:** Tras la fusión de origin/main (03-11) nuestro Publish/Snapshot se descartó a favor del PermissionRangeTree de upstream; =0 ya no restaura el comportamiento original sino el cerrojo sobre el árbol vivo.

Fuentes: 3d7ca311 2026-10-10, 3d7ca311 2026-11-03, commit 691b7e77

<a id="prereserve_min"></a>
#### `PRERESERVE_MIN` — ON, def. 64K Win, 16M POSIX

Razonamiento: El streamer HDD de GTA caía (exit=139) tras sceKernelBatchMap con MAP_FIXED fallido: la región destino era MEM_RESERVE de otro uso del host (pilas, segmentos de heap) en huecos <16 MiB que el arranque no reservaba. Se prerreservan todos los huecos ≥64 KB en Windows. El lote de 13 sesiones dio 0 caídas en ambos brazos y las regiones ajenas crecían igual (41–58), así que el arreglo era insuficiente y se acabó resolviendo con CHILD_ASLR=low. *[estabilidad]*

**Contradicción:** El registro lo presenta como el arreglo; el A/B de 13 sesiones no distinguió brazos (0 caídas en ambos, regiones ajenas crecían a 41–58 en los dos) y «ayuda solo marginalmente»; el arreglo real fue CHILD_ASLR=low.

Fuentes: 3d7ca311 2026-10-14, 3d7ca311 2026-10-15, commit b9e25aa6

<a id="recorder_keep"></a>
#### `RECORDER_KEEP` — ON, def. 1048576

Razonamiento: En paneo las caminatas registraban más de 16.384 lecturas, así que la lista del grabador se rehacía desde cero en cada fallo (~160 MB/s, 43 % de lo asignado). Ahora conserva hasta 1M entradas (40 MB). Upstream acabó con un tope equivalente de la lista (0f3ce7233a, 4ea927cec2). Solo medido dentro del paquete de recortes de asignación. *[ganancia]*

Fuentes: 3d7ca311 2026-10-13, 3d7ca311 2026-11-06, commit 04380721

<a id="recorder_keysort"></a>
#### `RECORDER_KEYSORT` — ON, def. on

Razonamiento: Las lecturas grabadas se ordenaban con un Comparison sobre tuplas de 40 bytes (5,4 ms/frame del front en paneo según el commit); se ordenan con un array de claves ulong y otro de índices (mismo introsort, mismo orden, empates incluidos). La equivalencia se comprobó en 225 casos aleatorios sobre el runtime de pwsh, no se repitió en .NET 10. Forma parte del paquete que dio +0,85 FPS estático. *[ganancia]*

**Contradicción:** El registro marca la incoherencia 3,4 vs 5,4 ms: el commit 04380721 dice 5,4 ms/frame; la cifra de 3,4 del comentario no aparece en las transcripciones.

Fuentes: 3d7ca311 2026-10-13, commit 04380721

<a id="scratch_pool_indexed"></a>
#### `SCRATCH_POOL_INDEXED` — ON, def. on

Razonamiento: Parte del paquete perdraw (c5813da2) para recortar coste por draw en el render: los cubos de longitudes cortas del pool de scratch pasan a un array indexado en vez de un diccionario. El paquete bajó el código gestionado 2,06 ms/frame (Dictionary TryGetValue 1,49 → 0) con 0 discrepancias; el interruptor no tiene A/B propio. *[sin-A/B]*

Fuentes: 3d7ca311 2026-10-25, commit c5813da2

<a id="single_view_writes"></a>
#### `SINGLE_VIEW_WRITES` — ON, def. on

Razonamiento: Idea de upstream #1082 (adaptada de 2838b258, Berk): una escritura o copia cubierta por una sola vista del backing copia por su alias sin el cerrojo de región; los ganchos de seguimiento corren igual. Junto a HLE_SPAN_WRITES y STRIPED_ACCESS_COUNT (interruptores M) se encendió el 17-10; antes se pidió ablación por interruptor y un argumento de corrección sobre orden/visibilidad frente a un cuelgue visto con M, que el registro dice que también sale sin M. *[ganancia]*

**Contradicción:** El primer A/B del 15-10 (arm M) fue peor en estática (20,23 vs 20,90) porque las esperas de GPU se duplicaban; el +3-4 FPS del registro es de la integración del 17-10.

Fuentes: 3d7ca311 2026-10-15, 3d7ca311 2026-10-17, commit 519e4d5c

<a id="spanset_add_contained"></a>
#### `SPANSET_ADD_CONTAINED` — ON, def. on

Razonamiento: Parte del paquete perdraw: SpanSet.Add de un rango ya contenido quitaba y reinsertaba el mismo span; ahora vuelve sin tocar nada. Lleva un test aleatorio contra un modelo de cobertura y el paquete dio 0 discrepancias en verify, pero no tiene A/B propio. *[sin-A/B]*

Fuentes: 3d7ca311 2026-10-25, commit c5813da2

<a id="span_snapshot"></a>
#### `SPAN_SNAPSHOT` — ON, def. on

Razonamiento: Covers tomaba el cerrojo de lectura en cada consulta; se copió la idea de upstream #1082 (instantánea inmutable de los spans) con implementación propia. Va encendido porque el lote de 7 cambios de #1082 dio +0,49 FPS en A/B (16,23 vs 15,74) con audio ficticio; en la fusión del 3-nov se adoptó el SpanSnapshot de upstream y =0 sigue tomando el cerrojo. *[A/B-upstream]*

**Contradicción:** El registro dice «sin A/B de FPS», pero el informe del 12-oct da un A/B +0,49 FPS para el lote completo (no aislado).

Fuentes: 3d7ca311 2026-10-12, 3d7ca311 2026-11-03, commit 24ebb838

<a id="striped_access_count"></a>
#### `STRIPED_ACCESS_COUNT` — ON, def. on

Razonamiento: Cada lectura/escritura HLE de un solo vista incrementaba un contador compartido y los hilos del render rebotaban su línea de caché (microbench: 2,9 us por llamada con 6 hilos frente a 0,29 us con uno); se repartió en 64 franjas por hilo. El 15-oct el A/B del brazo M salió peor en estática (20,23 vs 20,90) y se dejó apagado porque las esperas a GPU se duplicaban por contrapresión; el 17-oct el muestreo mostró Subrender 30→10 ms y spin del cerrojo 4,5-7,6→0,4-0,8 y se encendió junto a SINGLE_VIEW_WRITES y HLE_SPAN_WRITES. *[adelanta-cuello]*

**Contradicción:** El 15-oct el brazo M (con este interruptor) dio estática 20,23 vs 20,90 de la base y se decidió mantenerlos apagados; el registro solo cita el +3-4 FPS posterior.

Fuentes: 3d7ca311 2026-10-15, 3d7ca311 2026-10-17, commit 3d88460a

<a id="fast_import_gateway"></a>
#### `FAST_IMPORT_GATEWAY` — OFF, def. off

Razonamiento: Dentro de agc-hle: abaratar el gateway de imports para los constructores Agc (18 consultas de tamaño constantes como stubs nativos y despacho hoja). Nunca se midió aislado: en el lote A/B el brazo H (con STORE_TABLE_CHUNKS y los interruptores de memoria) quedó peor en estática (20,11 vs 20,90) porque el juego pasaba a enviar antes y las esperas de GPU se duplicaban (GCW 4,0→9,5 ms); se quedó apagado. *[sin-A/B]*

**Contradicción:** El registro dice «sin medir»; solo se midió dentro del brazo H, mezclado con CHUNKS y los interruptores de memoria.

Fuentes: 3d7ca311 2026-10-14, 3d7ca311 2026-10-15, commit ab855204

<a id="fixed_map_conflict"></a>
#### `FIXED_MAP_CONFLICT` — OFF, def. off

Razonamiento: Diagnóstico del paquete mapeo2: si un mapeo fijo rechazado solapa memoria host ajena (un clúster de ~14 MiB con un heap y 8 pilas dentro de la ventana, ~0,5 % de cuelgues por arranque), falla en seco con FATAL en vez de que el juego escriba encima. Se reprodujo con TEST_HOST_BLOCKER; el brazo RF que lo ejercitaría quedó opcional y no hay resultado registrado. *[estabilidad]*

Fuentes: 3d7ca311 2026-10-15, commit 5a6ea6b1

<a id="hle_warm_parallel"></a>
#### `HLE_WARM_PARALLEL` — OFF, def. off

Razonamiento: Mejora de arranque de upstream (294fb81f): JIT paralelo de los métodos HLE. Con las tres de arranque en =1 el primer fotograma llega a los 7,9 s en vez de 20,5, sin nada en contra, pero solo en 2 sesiones: pocas para cambiar el defecto, así que se integra opt-in y el arranque por defecto sigue siendo el medido antes de la fusión. *[A/B-upstream]*

Fuentes: 3d7ca311 2026-11-05, commit 480aced2

<a id="redzone_cache"></a>
#### `REDZONE_CACHE` — OFF, def. off

Razonamiento: Una de las tres mejoras de arranque de upstream (294fb81f): caché en disco del plan de parches de zona roja. Con las tres en =1 el primer fotograma llega a los 7,9 s en vez de 20,5 y 2 sesiones no mostraron nada en contra, pero son pocas para cambiar el defecto; queda opt-in y el arranque por defecto es el medido antes de la fusión. R2R no se midió (solo actúa al publicar). *[A/B-upstream]*

Fuentes: 3d7ca311 2026-11-05, commit 480aced2

<a id="tls_scan_batch"></a>
#### `TLS_SCAN_BATCH` — OFF, def. off

Razonamiento: Mejora de arranque de upstream (294fb81f): el arranque con las tres opciones =1 baja de 20,5 a 7,9 s hasta el primer fotograma. Se dejó nuestro arranque por defecto porque solo había 2 sesiones largas sin mecanismo en contra y no bastan para validar el defecto encendido; R2R no se pudo medir sin publicar. *[A/B-upstream]*

Fuentes: 3d7ca311 2026-11-05, commit 480aced2

<a id="image_device_budget"></a>
#### `IMAGE_DEVICE_BUDGET` — Descartado, def. off

Razonamiento: Portado de DarthPotato 5a4bf244: umbrales de recolección de imágenes según la VRAM (margen 1/8, 60/85/92 %) en vez de 1536/1536/3072 MiB fijos. Se deja apagado porque en una GPU de 16 GB dejaría crecer las imágenes hasta ~11 GB, con solo el margen para búferes y otros procesos, y no se había medido en GTA. *[otro]*

Fuentes: 3d7ca311 2026-11-06, commit acb01194

<a id="protection_block_shift"></a>
#### `PROTECTION_BLOCK_SHIFT` — Descartado, def. 22

Razonamiento: El perfil dejó dos esperas de cerrojo de ~1,6 ms/frame en el render; una era el cerrojo de protección del host por bloques de 4 MB (256 cerrojos) compartido con los hilos del guest. Hipótesis: bloques de 64 KB con 4.096 cerrojos (rt439) reducirían la espera; sin ganancia (12,34 vs 12,38) y la sonda de cerrojos mostró sin contención real, así que se deja 4 MB/256. *[exacto-neutro]*

Fuentes: 3d7ca311 2026-10-10, commit e6e84e1f

<a id="protection_locks"></a>
#### `PROTECTION_LOCKS` — Descartado, def. 256

Razonamiento: Mismo experimento que PROTECTION_BLOCK_SHIFT (rt439): probar 4.096 cerrojos de protección con bloques de 64 KB para reducir la espera del render; sin ganancia (12,34 vs 12,38), se mantienen 256 y 4 MB. *[exacto-neutro]*

Fuentes: 3d7ca311 2026-10-10, commit e6e84e1f

<a id="protect_outside_lock"></a>
#### `PROTECT_OUTSIDE_LOCK` — Descartado, def. 0

Razonamiento: Hipótesis: los workers de subidas diferidas esperan al cerrojo de región mientras NtProtectVirtualMemory corre dentro; sacar la llamada fuera con una puerta por región es exacto (verify 0 en ~15 M páginas, cerrojo 4,2→0,2 ms) pero neutro en FPS. Es uno de los palancas exactas agotadas para los fotogramas perdidos del paneo, cuyo cuello es la CPU de grabación del render, no estos workers. *[exacto-neutro]*

Fuentes: 3d7ca311 2026-11-03, commit 2e3a1f3d, commit 44889f54

<a id="alloc_survey"></a>
#### `ALLOC_SURVEY` — Sonda, def. off

Razonamiento: El render de GTA asignaba 4,45 MB por frame (2,3 MB en DescriptorPreparation); la sonda de tests muestrea los tipos asignados para encontrar las fuentes en vez de adivinar. Permitió agrupar TextureResource y compañía en SHARPEMU_RENDER_POOLS y localizar una clausura de ProbeBvhRefitBuffer que reservaba 56 B por búfer incluso con las sondas apagadas. *[coste-sonda]*

Fuentes: 3d7ca311 2026-10-16, 3d7ca311 2026-10-23, commit 5dbfcd99

<a id="test_host_blocker"></a>
#### `TEST_HOST_BLOCKER` — Sonda, def. off

Razonamiento: Choque raro (~0,5 % por arranque) del mapeo fijo del guest con reservas del host: pilas de hilos del CLR dentro de la ventana del guest antes de cualquier código del emulador. Se creó un reproductor que reserva memoria en una dirección dada para hacer el fallo determinista y probar la palanca SHARPEMU_CHILD_ASLR=low; los brazos m3*R eran ese bloqueador inyectado, no fallos naturales. *[otro]*

Fuentes: 3d7ca311 2026-10-15, 3d7ca311 2026-10-17, commit 5a6ea6b1

### sondas/diagnóstico (149)

<a id="dcc_clear_timing_gate"></a>
#### `DCC_CLEAR_TIMING_GATE` — ON, def. on

Razonamiento: Parte del paquete de recortes por draw (gta/perdraw): las dos marcas QPC por búsqueda DCC solo se toman si el informe de perfil está activo, ya que hay decenas de miles de draws por segundo. El paquete entero recortó ~2,06 ms/fotograma de código gestionado en el render (~1,25 µs por draw+dispatch) con 0 discrepancias de verify, y se integró activo; el interruptor no se midió por separado. *[ganancia]*

Fuentes: 3d7ca311 2026-10-25, commit c5813da2

<a id="table_word_memo_audit"></a>
#### `TABLE_WORD_MEMO_AUDIT` — ON, def. 64

Razonamiento: LEARN aprende qué escritores dejan una palabra igual pero no es exacto por construcción; AUDIT es la red de seguridad (compara 1 de cada n aciertos y veta la palabra si falla). Se encendió en 64 junto a LEARN=8 y PRECISE_WRITE_TICKS tras A/B: paneo 29,68 vs 28,79, 0 discrepancias. *[corrección]*

Fuentes: 3d7ca311 2026-11-01, 3d7ca311 2026-11-02, commit 0cb22c4a

<a id="260f_written"></a>
#### `260F_WRITTEN` — OFF, def. off

Razonamiento: Con 0x260F en el dispositivo el BVH se rompía porque sus stores van por V# construidos con punteros de su propia tabla y ningún rango planificado los cubría, así que esos destinos ni se registraban ni se marcaban escritos por GPU y los lectores veían datos rancios. El interruptor los registra, pero solo funciona porque lee el recuento con una lectura de CPU que espera a la GPU, y la espera se muda a 994A (6,3 vs 7,8 ms), de modo que la cadena no se cerró. *[adelanta-cuello]*

Fuentes: 3d7ca311 2026-10-18, 3d7ca311 2026-10-22, commit 6a544bd2

<a id="matblock_probe"></a>
#### `MATBLOCK_PROBE` — Descartado, def. off

Razonamiento: Hipótesis: una generación de escritura por bloque de 4 KB podría saltarse validaciones de la materialización en el front. El techo medido es ~1,7 ms/s de las validaciones seguras (41 % de ellas), muy por debajo del umbral de 2 ms/frame y optimista, así que no se implementó y se descartó. *[exacto-neutro]*

Fuentes: 3d7ca311 2026-10-17, commit 4cde5c6d

<a id="overlay_apply_skip"></a>
#### `OVERLAY_APPLY_SKIP` — Descartado, def. vacío

Razonamiento: Hipótesis: las cabeceras de blobs parcheadas por la superposición causaban los ciclos BLAS. No parchear en esas regiones dio 59 y 62 blobs con ciclo, así que no son la causa; el interruptor se quedó como experimento descartado. *[corrección]*

Fuentes: 3d7ca311 2026-10-10

<a id="queue2_probe"></a>
#### `QUEUE2_PROBE` — Descartado, def. off

Razonamiento: Midió el techo de una 2.ª VkQueue: ~0,9 ms paneo y ~0,8 estática, bajo el umbral de 1,5. La alternativa sin segunda cola (que el front dé preferencia a cabezas de compute desbloqueadas) tiene techo ~0,6 ms. La espera es GPU gfx pendiente (pases RT), así que la palanca es abaratar esos shaders y no reordenar colas. *[otro]*

Fuentes: 3d7ca311 2026-10-29, commit a2dcf03d

<a id="ab_dump_hash"></a>
#### `AB_DUMP_HASH` — Sonda, def. 0

Razonamiento: Sonda para comparar por volcado, programa a programa, las entradas y salidas de 0x260F en el dispositivo frente a la base y localizar dónde se rompe el BVH. En el primer dispatch coinciden y desde el segundo ya difieren las entradas, pero el propio método de volcado da artefactos (volcar un rango a medias lo registra y lo sube; leer como CPU no es fiable), así que no se aisló el primer byte malo; haría falta volcado por copia GPU, 1-2 días sin garantía. *[otro]*

Fuentes: 3d7ca311 2026-10-18, commit d5062a3d

<a id="append_probe"></a>
#### `APPEND_PROBE` — Sonda, def. off

Razonamiento: Sonda de la investigación del BLAS/refit (junto a REFIT_PROBE, REFIT_STEPS y HDR_PROBE): escribe el popcount de EXEC y el append de la lista de 0x7D56 en el relleno de la cabecera (+0xB0…+0xDC) para observar el programa. Sin veredicto propio en las transcripciones. *[otro]*

Fuentes: 3d7ca311 2026-10-09

<a id="args_writer_inject"></a>
#### `ARGS_WRITER_INJECT` — Sonda, def. 0

Razonamiento: Herramienta de prueba de ARGS_WRITER_PROBE: infla en 2^24 el +140 del n-ésimo escritor de D8A00 (~0,2 s de grupos que salen pronto) para demostrar de punta a punta que el informe detecta el fallo. En d98inj1 el escritor salió marcado StaleHeader/Big y el consumidor se casó con su productor. *[otro]*

Fuentes: 3d7ca311 2026-11-03, commit 7364528c

<a id="args_writer_probe"></a>
#### `ARGS_WRITER_PROBE` — Sonda, def. off

Razonamiento: El cuelgue D9800 (2/331 sesiones) tiene como causa aparente un recuento gigante que D9800 recibe como argumentos indirectos escritos por D8A00; las barreras son correctas, así que la hipótesis principal es contenido CPU→GPU rancio en la entrada de D8A00 (se vio una vez ceros en GPU donde la CPU tenía datos, 1 de cada ~840 escrituras). La sonda copia en GPU lo que lee D8A00, lo compara con la CPU y solo imprime anomalías, sin coste visible de FPS; se dejó apagada y el remojo awp01-12 salió limpio. No combinar con INDIRECT_ARGS_PROBE porque su ring perezoso pisa el informe. *[estabilidad]*

Fuentes: 3d7ca311 2026-11-03, commit 3368666f, commit 7364528c

<a id="attach_probe"></a>
#### `ATTACH_PROBE` — Sonda, def. off

Razonamiento: Medir si un atajo en AcquireAttachments para draws que repiten los attachments del anterior (84,5 % de los draws) compensaba: techo ~1,8 ms/fotograma, por debajo del umbral de 2 ms, y el atajo no podría ahorrarlo todo (hay que seguir comprobando escrituras de CPU, DCC/HTile y propiedad), ~1-1,4 ms reales. Se hizo de todos modos la fase 2 (ATTACH_REPEAT, -0,5 ms exacto, con 0 mismatches), que upstream sustituyó luego con InPlaceDraw/TryRestoreAttachments; para los workers de slices la sonda se acotó con !_worker por ser estática no atómica. *[exacto-neutro]*

Fuentes: 3d7ca311 2026-10-17, 3d7ca311 2026-10-18, 3d7ca311 2026-11-03, 3d7ca311 2026-11-07, commit 8ef785df, commit dc0ec36f

<a id="back_materialize_stacks"></a>
#### `BACK_MATERIALIZE_STACKS` — Sonda, def. off

Razonamiento: Diagnóstico de por qué el hilo de render materializaba ~245 programas por fotograma: la cadena BVH de GTA V (diez programas 64x1x1, ~80 dispatches) no tenía ShaderData y el render leía los recuentos y volvía a preparar el programa en cada dispatch. Con las pilas se halló la causa y se arregló con INDIRECT_LIMITS_IN_SHADER_DATA (por defecto): sync 11,2→8,8 ms, materializaciones ~245→~165 y FPS 15,33→15,49. *[ganancia]*

Fuentes: 3d7ca311 2026-10-11, commit e6b103b7

<a id="batch_sim"></a>
#### `BATCH_SIM` — Sonda, def. off

Razonamiento: Ejecución en seco (P0 del plan de transiciones diferidas) de la grabación por lotes: cuenta qué tamaño tendrían los lotes si el layout de imágenes se decidiera por lote, sin grabar nada diferente. Es la medida que decide si la vía estructural P4/P5 (3-5 semanas) merece la pena: se abandonaba si menos de ~40-50 % de los draws caían en lotes de 8 o más. Más tarde hubo que acotarla con !_worker para los workers de slices. *[otro]*

Fuentes: 3d7ca311 2026-10-11, commit 6b0ddf13

<a id="bench_agc"></a>
#### `BENCH_AGC` — Sonda, def. off

Razonamiento: Activa un micro-benchmark explícito (AgcHleBenchmark, ns por llamada) de las rutas de escritura HLE de AGC y de la pasarela de importación rápida, para decidir si valía la pena dejar los constructores de Agc en stubs nativos. Medido: sceAgcDcbJumpGetSize costaba 233 ns por la pasarela frente a 8 ns del export; la ganancia en juego (-8 a -25 ms de CPU por hilo Subrender) era solo una estimación sin medir. *[sin-A/B]*

Fuentes: 3d7ca311 2026-10-14, commit 7c8b8fa7

<a id="binding_repeat_log"></a>
#### `BINDING_REPEAT_LOG` — Sonda, def. off

Razonamiento: Medir cuánto se repiten las entradas de PrepareBindings para valorar un memo de la preparación por draw. Resultado: solo 1,1-1,2 % igual al fotograma anterior y ~18 % repetida dentro del fotograma (imágenes y samplers 98-99 %), con lo que un memo salvaría ~2 ms como mucho. Una estimación previa de 5-8 ms salía del 81,6 % de ATTACH_REPEAT, que mide los destinos (que ya tienen memo) y no las entradas de bindings. *[otro]*

Fuentes: 3d7ca311 2026-10-11, 3d7ca311 2026-10-23, commit 68f6d4a2

<a id="cheap_probe"></a>
#### `CHEAP_PROBE` — Sonda, def. off

Razonamiento: Sonda para decidir con datos tres mecanismos baratos de upstream (PR #1128 de TarkusR): espera de ranura de readback, escaneo BDA sobre granulos mapeados y SET_PREDICATION. GTA V nunca encontró ranuras ocupadas (SLOT_WAIT apagado), no envía SET_PREDICATION, y el escaneo BDA bajó de ~157 a ~3 µs de GPU (BDA_SCAN_MAPPED por defecto); el +3,4 FPS en paneo del A/B (28,04 vs 24,64) probablemente es sobre todo ruido. *[A/B-upstream]*

Fuentes: 3d7ca311 2026-10-29, commit e76f182f

<a id="cmdmem_log"></a>
#### `CMDMEM_LOG` — Sonda, def. off

Razonamiento: Averiguar qué era CommandMemoryTransfer (~5 ms/fotograma): rellenos DMA de ~21 MB/fotograma (~1 GB por ventana de 5 s) aplicados como memset de CPU sobre memoria que ningún búfer o imagen GPU cubre aún. Las vías opt-in SHARPEMU_FAST_FILL y FILL_ON_GPU_MIN son correctas pero no alcanzan esos rellenos, así que se quedaron apagadas. *[exacto-neutro]*

Fuentes: 3d7ca311 2026-10-10, commit f7ab3b87

<a id="coldres_probe"></a>
#### `COLDRES_PROBE` — Sonda, def. off

Razonamiento: Hipótesis: los ~8,6 ms de exceso de CommandStream en fotogramas perdidos eran resolución en frío del estado compartido. Midiendo con la sonda resultó ser volumen (draws +16-24 % a ~18,5 µs, dispatches +24-31 % a ~30 µs); lo único frío son esperas de readback dentro de fallos de materialización (+1,6-1,8 ms), una dependencia GPU→CPU real, y las creaciones y cerrojos son despreciables. Sin palanca exacta ≥2 ms: integrado apagado y paneo ~29,8 aceptado. *[otro]*

Fuentes: 3d7ca311 2026-11-03, commit 2012aaa2, commit ecdbdf78

<a id="compile_log"></a>
#### `COMPILE_LOG` — Sonda, def. off

Razonamiento: Medir qué compilaciones de shader o pipeline bloqueaban a mitad de partida: D44E crea 15 pipelines (4 fallos en frío de ~3,3 s) que el dispatch espera en el hilo de render, 13,2 s bloqueados en 150 s (25,73 FPS). De ahí salió INDIRECT_IMAGE_PAD (exacto, sin coste, máximo 4 compilaciones), y el A/B mostró que sin ninguna compilación el paneo base es 27,0-28,3, así que lo que falta para 30 no son compilaciones. En frío (2026-11-05) también confirmó que la condición de upstream de esperar pipelines nunca se ejecuta en GTA V. *[otro]*

Fuentes: 3d7ca311 2026-10-30, 3d7ca311 2026-11-05, commit 4c08ebd8, commit a9e377a0

<a id="cp_write_defer_probe"></a>
#### `CP_WRITE_DEFER_PROBE` — Sonda, def. off

Razonamiento: Pregunta: ¿se pueden diferir o fusionar los WRITE_DATA del CP al final del scope para no romper pasadas de rendering? Medido: ~290 WRITE_DATA/fotograma (98 % de 16 B o menos), solo 6,6 % seguidos y 0,8 contiguos (fusionar no da nada), y diferir evitaría ~10 roturas/fotograma (~0,1 ms); quitar la barrera previa a cada copia rendiría ≤0,3-0,4 ms. No se implementó nada. *[otro]*

Fuentes: 3d7ca311 2026-10-17, commit 59786026

<a id="da_unchecked"></a>
#### `DA_UNCHECKED` — Sonda, def. off

Razonamiento: Cota inferior no exacta del coste de la búsqueda en la tabla de páginas de direcciones de dispositivo (sin comprobar el índice, sin rama y sin registro de fallos): dispatch ~16,5→13,3 ms (n=3). Por no ser exacta nunca puede ser defecto; las variantes exactas (BRANCHLESS, FAULT_MODE 2/4, ZP_TAGGED+ACC=4) no ganan nada porque solo hay ganancia si desaparecen las dos ramas a la vez. De ahí salió DA_ZERO_PAGE, que sí pasó a defecto. *[exacto-neutro]*

Fuentes: 3d7ca311 2026-10-17, commit c8e09b20

<a id="da_vsharp_modes"></a>
#### `DA_VSHARP_MODES` — Sonda, def. off

Razonamiento: Hipótesis descartada en la investigación de por qué 0x260F/994A en el dispositivo rompen el BVH: que los V# de descriptores de dispositivo usaran swizzle/ADD_TID u OOB_SELECT de forma distinta. Encontró 0 V# con swizzle o ADD_TID y ~2 % con OOB_SELECT=3 y stride, que la ruta de dispositivo acotaba a records×stride; con DA_VSHARP_OOB=1 el BVH seguía roto. *[otro]*

Fuentes: 3d7ca311 2026-10-18, commit d5062a3d

<a id="deferred_protect_verify"></a>
#### `DEFERRED_PROTECT_VERIFY` — Sonda, def. off

Razonamiento: Comprobar que el camino de subidas diferidas, con o sin PROTECT_OUTSIDE_LOCK, no pierde escrituras. Una página que difiere se vuelve a comprobar 2 ms después sin cerrojos, separando marca tardía o escritura por el backing (BackingWriteLog: CP y descargas dejan páginas limpias a propósito) de una escritura realmente perdida; en paneo hubo 0 perdidas. En ese trabajo HOT_PRESERVE salió negativo (copias 0,6→12 ms/fotograma, ~19 FPS). *[corrección]*

Fuentes: 3d7ca311 2026-11-02, commit 44889f54

<a id="desc_dedup_probe"></a>
#### `DESC_DEDUP_PROBE` — Sonda, def. off

Razonamiento: Palanca 3 de gta/descdedup: deduplicar commits de descriptores que repiten el anterior. Aunque el 64-71 % de los commits tienen descriptores vigentes, solo el 1,1-3,6 % repite contenido, y lo ahorrable sale a ~0,011-0,02 ms/fotograma frente al mínimo de 1,5 ms pedido, así que no se implementó y solo se dejó la sonda. *[otro]*

Fuentes: 3d7ca311 2026-10-18, commit 104e16c2

<a id="device_data_reads_verify"></a>
#### `DEVICE_DATA_READS_VERIFY` — Sonda, def. off

Razonamiento: Modo verify del paso que lee las palabras de datos de la tabla por la tabla de páginas en la GPU en vez de esperar al host (quitar esperas síncronas): 0 discrepancias en 585.300 y luego 805.038 comprobaciones. El A/B del paquete fue neutro (paneo 28,65 vs 28,65): cada lectura quitada adelanta la siguiente (sale F5EB +0x50 y pasa a F5EB +0x58 3,33 ms y 9FBC +0x50 1,74), porque sus palabras las cubren rangos escritos de 40 MB del lote abierto. Propuesta: no activar nada por defecto y atacar F5EB/4481. *[adelanta-cuello]*

Fuentes: 3d7ca311 2026-10-31, commit a9b95c25

<a id="device_lost_trace"></a>
#### `DEVICE_LOST_TRACE` — Sonda, def. off

Razonamiento: Aparecieron 4 DeviceLost en ~12 sesiones de paneo (frente a 1 en 1.716 antes) y la sospecha del coordinador era el TLAS HLE; la traza guarda los últimos 1.024 draws, dispatches y envíos y, con VK_EXT_device_fault, las direcciones que fallaron. Un DeviceLost también ocurrió sin el worker (WORKER=0), así que era un fallo previo destapado por el paneo; se añadió espera de 20 s a los demás hilos para que termine el informe. *[estabilidad]*

Fuentes: 3d7ca311 2026-10-23, commit 24d84b7a, commit d1c7870d

<a id="dirty_watch"></a>
#### `DIRTY_WATCH` — Sonda, def. 0

Razonamiento: Averiguar quién ensucia las palabras que el render lee síncronamente de memoria escrita por GPU (~4,4 ms/fotograma, ~4,6 lecturas de ~0,9 ms), para saber si son dependencias reales o marcas de más. Una auditoría vieja decía que 0xF43B y 0x994A nunca cambian al sincronizar (sobremarcadas) y 02F1/B198 siempre cambian (dependencia real). Lo usado junto a MERGE=0 daba 11,9 FPS, por lo que es una sonda cara. *[coste-sonda]*

Fuentes: 3d7ca311 2026-10-12, 3d7ca311 2026-10-17

<a id="dispatch_cpu_probe"></a>
#### `DISPATCH_CPU_PROBE` — Sonda, def. off

Razonamiento: Valorar un refit HLE de BLAS: la premisa «~1.300 dispatches / 13 ms» era la escena de ciudad de TheCruZ (~170 BLAS), mientras que el banco tiene 8 BLAS y 89-90 dispatches de la cadena. La cadena cuesta 1,6-1,8 ms de CPU del render y ~0,3 ms de GPU fuera del camino crítico (paneo CPU-limitado, GPU ociosa 12,5 ms), y hay precedentes de que quitar CPU de render no da FPS (FRONT_SPECULATE -1,6 ms, 29,74 vs 29,71); no merece la pena. *[exacto-neutro]*

Fuentes: 3d7ca311 2026-11-06, commit 7afd50cd, commit aef11304

<a id="dispatch_exec_log"></a>
#### `DISPATCH_EXEC_LOG` — Sonda, def. off

Razonamiento: 73238bfa imprimía [DISPATCH_EXEC] sin condición para cada dispatch 1x1x1 (hasta 600 líneas) y rompía RenderExecutorStateTests.TraceOff_ProducesNoLines (+432 B en stderr en las 3 plataformas, no era ruido). Se puso detrás de =1 en ci/arreglos-tests; ya se mantiene en el portado del recorder. *[corrección]*

Fuentes: 3d7ca311 2026-10-11, commit 070743b3, commit b6949768

<a id="dump_spirv"></a>
#### `DUMP_SPIRV` — Sonda, def. off

Razonamiento: Volcado de SPIR-V filtrado por hash para inspeccionar shaders concretos: el 2026-10-07 se usó para el compute 0x20B9 (3,15 MB de SPIR-V), y se sugirió de nuevo para D44E (IncompatibleImageCandidates, 2026-10-30) y para diferenciar la traducción real de FADB entre ramas (2026-11-04). *[otro]*

Fuentes: 3d7ca311 2026-10-07, 3d7ca311 2026-10-30, 3d7ca311 2026-11-04

<a id="exact_written_ranges"></a>
#### `EXACT_WRITTEN_RANGES` — Sonda, def. no leído

Razonamiento: Es el concepto de los rangos escritos exactos que declara un dispatch (NoteDeclared), no un interruptor leído: WriteDiffProbe los compara con lo que cambió realmente y, con SHARPEMU_WRITE_DIFF_EXACT=1, cuenta los bytes cambiados fuera de ellos ([WRITE_DIFF_EXACT] outside=...), que deben ser 0. *[otro]*

Fuentes: 3d7ca311 2026-10-31

<a id="fault_insn_probe"></a>
#### `FAULT_INSN_PROBE` — Sonda, def. off

Razonamiento: Dimensionar una emulación de la carga que falla (FAULT_READ_EMULATE): cuenta los fallos de lectura en páginas vigiladas por dirección de instrucción del guest, con la instrucción decodificada, páginas distintas y una dirección de fallo, cada 20 s. Se lanzó en estática y paneo con READBACK_AUDIT (la sesión estática fiS dio 25,02 FPS con las sondas). *[otro]*

Fuentes: 3d7ca311 2026-10-22, commit b374d189, commit e65905ca

<a id="fault_read_sites"></a>
#### `FAULT_READ_SITES` — Sonda, def. off

Razonamiento: FAULT_READ_EMULATE=1 emulaba toda lectura sin modificar y dejaba sucias para siempre las páginas GPU-dirty que la descarga habría limpiado, así que fallaban en cada lectura (0x57B67Dxxx ~1.700/s; sesiones estáticas a 13,7 y 16,4 FPS con el front bloqueado). Se cambió a emular solo si un escritor sigue en vuelo o la misma carga falla de nuevo, y la sonda cuenta las lecturas que fallan por dirección, instrucción y resultado. *[modo-lento]*

Fuentes: commit 775ed6d3

<a id="fill_log"></a>
#### `FILL_LOG` — Sonda, def. off

Razonamiento: CommandMemoryTransfer costaba ~5 ms/fotograma; la sonda mostró que son rellenos DMA (~21 MB/fotograma) aplicados como memset de CPU sobre memoria que ningún búfer o imagen GPU cubre aún. Las rutas opt-in FAST_FILL y FILL_ON_GPU_MIN son correctas pero no alcanzan esos rellenos, así que quedaron apagadas. *[otro]*

Fuentes: 3d7ca311 2026-10-10, commit f7ab3b87

<a id="flip_probe"></a>
#### `FLIP_PROBE` — Sonda, def. off

Razonamiento: Se creó para medir por tramos la vida de un flip (frente, render, GPU, FlipDone) y el ritmo del presentador; sirvió para comparar GATE_PREFETCH (peor: frente a render 9,6-10,7 vs 6,1-6,6 ms) y después para comparar sesiones rápidas y lentas en la investigación del modo lento. *[otro]*

Daniel: Encargo del 24-10: abrir rama gta/slowmode y comparar una sesión rápida con una lenta usando FLIP_PROBE y LABEL_PROBE para hallar qué dispara el lockstep.

Fuentes: 3d7ca311 2026-10-22, 3d7ca311 2026-10-24, commit 037cf1e4

<a id="fps_log"></a>
#### `FPS_LOG` — Sonda, def. off

Razonamiento: Medir FPS con el perfilador de fases cuesta ~0,5 FPS en GTA V, así que se añadió un contador mínimo que imprime el mismo formato RENDER_MS sin cronometrar fases. Pasó a ser la base de los A/B de FPS (los encargos exigen PROFILE_RENDER=0,PROFILE_GPU_DRAWS=0,FPS_LOG=1) y de SLOW_MODE_PROBE. *[otro]*

Fuentes: 3d7ca311 2026-10-11, 3d7ca311 2026-10-29, commit 531f5717

<a id="frame_miss_gpu"></a>
#### `FRAME_MISS_GPU` — Sonda, def. off

Razonamiento: Hipótesis: las ráfagas de fotogramas perdidos eran de GPU o de dependencias. Se añadió una línea temporal de GPU por presentación (con =2 solo tramos por búfer para reducir su coste); mostró que la GPU apenas cambia y lo que crece es la grabación del render (CommandStream +12,5 ms), de modo que el objetivo pasa a ser recortar grabación en fotogramas pesados. *[coste-sonda]*

Fuentes: 3d7ca311 2026-11-03

<a id="frame_miss_probe"></a>
#### `FRAME_MISS_PROBE` — Sonda, def. off

Razonamiento: Los huecos de paneo en ráfagas al aparecer contenido nuevo no tenían causa conocida; la sonda (=2 fases por draw, =3 piezas) desglosa en qué gasta el tiempo cada presentación perdida. Mostró +7-9 ms de traducción de draws y +5,5 ms de dispatches, con preparaciones de compute en el render en vez del frente, lo que motivó FRONT_SPECULATE y la investigación de grabación paralela. *[otro]*

Fuentes: 3d7ca311 2026-11-03, commit 3c4d50dd

<a id="front_prep_log"></a>
#### `FRONT_PREP_LOG` — Sonda, def. off

Razonamiento: Se añadió junto al overlay de escrituras pendientes del frente (que dejaba BLAS con ciclos en ~3 de 4 ejecuciones) para ver qué shader prepara el frente y cuáles son las lecturas parcheadas; el overlay quedó apagado por defecto y esta sonda es solo diagnóstico. *[corrección]*

Fuentes: 3d7ca311 2026-10-10, commit 57c543a0

<a id="front_prep_verify"></a>
#### `FRONT_PREP_VERIFY` — Sonda, def. 0

Razonamiento: El worker de preparación del frente (y los dispatch preparados fuera) podía dar resultados distintos del render; tras un DeviceLost con mismatch en 0x6A53 se creó este verify que re-prepara en el render y compara (=2 ejecuta con el del render). Condujo al arreglo exacto de la carrera de la caché de tablas (STORE_TABLE_APPLIED_CHECK) y demostró el worker exacto (0 mismatches en 8,8 M), aunque el worker pierde en estática y quedó apagado. *[exacto-neutro]*

Fuentes: 3d7ca311 2026-10-23, 3d7ca311 2026-10-24, commit 888f79e4, commit e88b8639

<a id="front_speculate_log"></a>
#### `FRONT_SPECULATE_LOG` — Sonda, def. off

Razonamiento: Acompaña a FRONT_SPECULATE (especular las lecturas del frente y reproducirlas en el render) imprimiendo la primera lectura que difirió al reproducir; el verify en paneo dio 0 mismatches y 26 k preparaciones conservadas cada 10 s, pero 0x5515 casi nunca coincide (el frente lee un valor rancio). *[exacto-neutro]*

Fuentes: 3d7ca311 2026-11-03, commit 22f86223

<a id="front_stale_check"></a>
#### `FRONT_STALE_CHECK` — Sonda, def. off

Razonamiento: El frente lee memoria directamente antes de que el render grabe la primera escritura de GPU de un rango; la sonda detectó lecturas rancias byte a byte (sobre todo con FRONT_PENDING_LABELS) y dio origen a FRONT_PREP_CHECK. Se usó para decidir PENDING_LABELS, que quedó opt-in porque pierde ~0,9 FPS en paneo (más preparaciones descartadas). *[corrección]*

Fuentes: 3d7ca311 2026-10-13, commit 85a17ed7

<a id="gate_probe"></a>
#### `GATE_PROBE` — Sonda, def. off

Razonamiento: Se creó para medir la espera/retención de la puerta de ShaderPipelineCache del frente; reveló ~9,5 ms/frame de espera. GATE_PREFETCH la baja a ~0,2 ms pero el render espera ~0,5 ms más y la estática cae ~0,6 FPS, así que ambos quedaron apagados. *[adelanta-cuello]*

Fuentes: 3d7ca311 2026-10-17, commit b34e0725

<a id="gpu_dump_start"></a>
#### `GPU_DUMP_START` — Sonda, def. 0

Razonamiento: Parte de las sondas del problema 0x260F (orden entre tabla de materiales y escrituras del CP), junto a TABLE_CP_PROBE y DA_HOST_BACKING_CP_ORDER; copia en GPU, en orden de stream, los rangos tras cada dispatch para ver lo que el dispositivo leyó. Se comiteó apagada, sin resultado propio. *[otro]*

Fuentes: 3d7ca311 2026-10-18, commit ca37fb6f

<a id="gpu_starve_probe"></a>
#### `GPU_STARVE_PROBE` — Sonda, def. off

Razonamiento: Para repartir el fotograma de paneo (~35-37 ms) entre GPU ocupada, trabajo sellado sin enviar y GPU ociosa. Halló GPU ociosa 11-13 ms, 3-3,5 ms por trabajo sellado que el hilo de envío no entregaba y 6,5 ms de render esperando vaciados; de ahí salió IDLE_FLUSH=4 (encendido) y la tarea B198 de 5981. Gira un núcleo y cuesta 1-2 FPS, por eso no va en A/B. *[coste-sonda]*

Fuentes: 3d7ca311 2026-10-31, commit be557443

<a id="guest_call_timeline"></a>
#### `GUEST_CALL_TIMELINE` — Sonda, def. off

Razonamiento: El modo lento no tenía causa; esta traza (faltas del guest sobre la etiqueta y llamadas HLE con duración) mostró que es una parada de 100 ms del front en un REWIND con el bit válido ya puesto, porque sceAgcRewindPatchSetRewindState no despierta al front y solo el reintento de 100 ms lo desbloquea. Motivó REWIND_PATCH_WAKE (WAKE=1/2 quedaban en ~27 FPS por esperas de readback; =3 conserva el ritmo del front). *[modo-lento]*

Fuentes: 3d7ca311 2026-10-26, 3d7ca311 2026-10-29, commit 2742c129, commit e4e7ee79

<a id="guest_read_split"></a>
#### `GUEST_READ_SPLIT` — Sonda, def. off

Razonamiento: Hipótesis: el render pasaba 5,2 ms/frame ocioso porque los hilos del juego esperaban ~25 readbacks por fotograma. La partición (relevo, inicio, GPU) mostró 0,09/0,06/0,73 ms: casi todo es GPU, por lo que enviar antes no ayuda y la hipótesis queda descartada. *[otro]*

Fuentes: 3d7ca311 2026-10-11, commit da4e32c4

<a id="hdr_probe"></a>
#### `HDR_PROBE` — Sonda, def. on

Razonamiento: Se montó para el pairs=0 de rt117-rt125; el registro dice que la hipótesis se retractó. En las transcripciones los lotes de medida fijan SHARPEMU_HDR_PROBE=0 en el entorno base, coherente con que esté encendida por defecto y haya que anularla a mano. *[otro]*

Fuentes: 3d7ca311 2026-10-12, 3d7ca311 2026-10-13

<a id="head_page_log"></a>
#### `HEAD_PAGE_LOG` — Sonda, def. off

Razonamiento: Registro asociado a IMAGE_HEAD_PAGE_WRITES, que no vigila hasta 8 páginas de cabecera de una imagen limpia: GTA escribe muchas páginas de sus imágenes de stencil cada fotograma, así que no hubo ganancia (rt393) y el interruptor quedó apagado. *[sin-A/B]*

Fuentes: 3d7ca311 2026-10-10, commit e828ff78

<a id="hotflags_file"></a>
#### `HOTFLAGS_FILE` — Sonda, def. off

Razonamiento: Cambiar un interruptor exige reiniciar el juego (~3,5 min de carga, jugador en otro sitio cada vez). Se diseñó un fichero NOMBRE=valor releído (<=250 ms) para alternar A-B-A-B cada ~30 s en la misma escena; diez interruptores son hot-safe, tres resetean estado en su hilo propietario y el resto se rechaza. Al cerrar la entrada, aún no se había comprobado en una partida real. *[otro]*

Daniel: Encargo: poder alternar interruptores en caliente para A/B en la misma escena sin recargar.

Fuentes: 3d7ca311 2026-11-06, commit 862d009c

<a id="hot_page_probe"></a>
#### `HOT_PAGE_PROBE` — Sonda, def. off

Razonamiento: Medir cuántas páginas CPU-sucias se suben y reprotegen (~1.520/fotograma, 97 % calientes) para probar PRESERVE_HOT_OBTAIN. Resultado: los obtains casi no aportan (-8 % de faltas); la reprotección viene del barrido BDA y lo que pesa es el coste de cada falta (~32 µs, ~7,4 ms/frame repartidos); FPS sin cambio. *[exacto-neutro]*

Fuentes: 3d7ca311 2026-10-18, commit c2b7b585

<a id="idle_probe"></a>
#### `IDLE_PROBE` — Sonda, def. off

Razonamiento: Para atribuir la espera ociosa del render (11,5-14,7 ms/frame) sin el perfilador. Casi toda es del tipo blocked (el front no entrega): el front espera al juego en REWIND (24-26 ms), no hay despertar perdido, y fue la base de la tarea gta/f43b y de los interruptores M de agc-hle. *[otro]*

Fuentes: 3d7ca311 2026-10-17, commit be0e9d11

<a id="image_lookup_probe"></a>
#### `IMAGE_LOOKUP_PROBE` — Sonda, def. off

Razonamiento: Comprobar si el memo de búsqueda de imágenes perdía por colisiones. LookUpImage cuesta ~1,45 ms/frame en todos los brazos; con 4096 ranuras + RANGE_LOOKUP_GENERATION + FULL_HASH solo se ahorran 0,08 ms, porque dominan lock y DCC del resto de FindImage; no cambiar nada. *[exacto-neutro]*

Fuentes: 3d7ca311 2026-10-22, commit b0524128

<a id="indirect_args_probe"></a>
#### `INDIRECT_ARGS_PROBE` — Sonda, def. off

Razonamiento: Cuelgue raro (2/331 sesiones) de 19-27 s en la ráfaga de BVH al empezar la misión, por un dispatch indirecto de D9800 con recuento enorme cuyos argumentos escribe D8A00. La sonda copia en GPU los 12 bytes de argumentos y la vista de entradas de D8A00; calibró que 2,7e8 grupos tardan ~3 s y 1,6e9 ~13 s. Sin reproducción en 13 sesiones dirigidas; se descartó MATERIALIZE_REBASE como causa (aunque se arregló su exactitud). *[estabilidad]*

Fuentes: 3d7ca311 2026-11-02, 3d7ca311 2026-11-03, commit bd10cef8, commit d6fba5fe

<a id="indirect_draw_probe"></a>
#### `INDIRECT_DRAW_PROBE` — Sonda, def. off

Razonamiento: Comprobar si GTA hace draws indirectos cuyos argumentos lee la CPU (para portar la ruta GPU de un PR upstream). En 300 s no salió ni una línea, así que no hay nada que ganar y no se portó (el cherry-pick tenía 6 conflictos). *[A/B-upstream]*

Fuentes: 3d7ca311 2026-10-22, commit f063b008

<a id="indirect_image_diag"></a>
#### `INDIRECT_IMAGE_DIAG` — Sonda, def. 0 (hash vacío)

Razonamiento: D44E (RT) se saltaba por IncompatibleImageCandidates; el diagnóstico encontró que su tabla de materiales recibe texturas de relleno 1x1 de otra clase (primero 3D, luego 2D UINT, y el 5-11 un 2D MSAA). Eso llevó a INDIRECT_IMAGE_DIM y al arreglo del magenta (entrada nula para T# multimuestra). *[corrección]*

Fuentes: 3d7ca311 2026-10-30, 3d7ca311 2026-11-05, commit ec532746

<a id="indirect_read_log"></a>
#### `INDIRECT_READ_LOG` — Sonda, def. off

Razonamiento: Los dispatch indirectos por hilos leían sus contadores en la CPU (esperas); el log nombró los shaders de la cadena BVH sin binding ShaderData. LIMITS_IN_SHADER_DATA=1 quita esas lecturas pero hunde a 1,9 FPS, por lo que quedó apagado. *[otro]*

Fuentes: 3d7ca311 2026-10-10, commit 09b5e0e0

<a id="inject_front_us"></a>
#### `INJECT_FRONT_US` — Sonda, def. off

Razonamiento: Sonda de equilibrio: inyecta espera activa en el front para medir cuánto de su tiempo está en el camino crítico. Con 2 ms inyectados la estática baja -0,28 ± 0,28 FPS (≈0,35, sin resolver) y el paneo no cambia; el A/B 3+3 solo resuelve recortes >=2,5 ms. *[otro]*

Fuentes: 3d7ca311 2026-10-14, commit 267d1111

<a id="inject_render_us"></a>
#### `INJECT_RENDER_US` — Sonda, def. off

Razonamiento: Sonda de equilibrio para estimar la sensibilidad del fotograma al hilo de render: 2 ms inyectados dan -0,37 ± 0,16 FPS en estática (cada ms quitado al render ≈0,45 ms de fotograma, rango 0,05-0,85) y nada en paneo. Se usó después para calibrar si el camino crítico dejó la CPU del render. *[otro]*

Fuentes: 3d7ca311 2026-10-14, commit 267d1111

<a id="label_poll_split"></a>
#### `LABEL_POLL_SPLIT` — Sonda, def. off

Razonamiento: Hipótesis: la descarga por falta de CPU del sondeo de la etiqueta esperaba detrás de otras copias en la cola de readback. La medida la refutó (cola 0,02 ms; la espera es el tick del escritor, 3,1-3,9 ms), así que no se construyó LABEL_POLL_QUEUE; solo se comiteó el diagnóstico. *[otro]*

Fuentes: 3d7ca311 2026-10-25, commit eadacec8

<a id="label_probe"></a>
#### `LABEL_PROBE` — Sonda, def. off

Razonamiento: Medir la vida de cada escritura del CP a la etiqueta 0x59C900000 hasta que el guest la ve (submit a front 21-23 ms, grabado, envío, GPU). Mostró que el modo lento no sale del lockstep inicial (1,2-1,5 ticks en vuelo vs 2,7-3,1 en rápidas) y que el juego sondea la etiqueta por un fallo de página que espera a toda la cola de GPU. *[modo-lento]*

Fuentes: 3d7ca311 2026-10-22, 3d7ca311 2026-10-24, commit 058714e6

<a id="label_timeline"></a>
#### `LABEL_TIMELINE` — Sonda, def. off

Razonamiento: Para decidir qué dispara el modo lento (primer flip, FlipDone, cola o carga) hacía falta una traza por eventos de la etiqueta. Mostró que la lenta tiene 0-2 ticks en vuelo y la rápida 3-5, con GPU ociosa ~420 vs 230-320 ms/s; llevó a RELAY_PUMP_IN_WAITS (encendido) y a FAULT_READ_EMULATE (exacto pero empeora). *[modo-lento]*

Fuentes: 3d7ca311 2026-10-24, 3d7ca311 2026-10-26, commit dd5e8f82

<a id="label_trace"></a>
#### `LABEL_TRACE` — Sonda, def. off

Razonamiento: Se creó para ver que las 4 etiquetas en que bloquea el front son escrituras de liberación entre colas aplicadas por el render 6-11 ms después (origen de FRONT_PENDING_LABELS) y luego para el análisis de camino crítico/lockstep. Sirvió para explicar por qué TABLE_WORD_MEMO_LEARN=8 baja el paneo: el render se adelanta, la cola GPU se alarga y el cuello pasa al front. *[adelanta-cuello]*

Fuentes: 3d7ca311 2026-10-13, 3d7ca311 2026-11-01, commit 4f5bdd78

<a id="label_trace_ticks"></a>
#### `LABEL_TRACE_TICKS` — Sonda, def. on (con LABEL_TRACE)

Razonamiento: Acompaña a LABEL_TRACE: un hilo espera cada tick en el timeline y anota TickDone para separar latencia lado GPU del reparto de la completion; cuenta además las esperas de readback por hilo y sitio de llamada. Se apaga con =0 si molesta. *[otro]*

Fuentes: 3d7ca311 2026-10-22, commit da4116f1

<a id="lock_attr_probe"></a>
#### `LOCK_ATTR_PROBE` — Sonda, def. off

Razonamiento: Atribuir por hilo quién reprotege páginas y quién retiene los cerrojos de región. Resultado: 4 hilos Subrender ~28.400 reprotecciones/s por fallos guest, el hilo de subidas diferidas ~2 ms/frame y el render ~0,5 ms/frame; ninguna palanca exacta llega a 2 ms, así que no se implementó nada. *[exacto-neutro]*

Fuentes: 3d7ca311 2026-10-18, commit a61dc93a

<a id="lock_probe"></a>
#### `LOCK_PROBE` — Sonda, def. off

Razonamiento: Buscar contención en cerrojos con nombre; el front pierde ~20 % en el _gate de ShaderPipelineCache pero no es el cuello. En el render no hay contención (cerrojo de imágenes 9 ms retenidos/fotograma y espera 0,04 ms; RegionLock es spinlock), y el reparto fino de cerrojos de protección no gana. *[exacto-neutro]*

Fuentes: 3d7ca311 2026-10-10, 3d7ca311 2026-10-11, commit e6e84e1f

<a id="materialize_miss_log"></a>
#### `MATERIALIZE_MISS_LOG` — Sonda, def. off

Razonamiento: Para entender por qué hay ~3.400 fallos de materialización/s. Mostró que casi todo es la cadena TLAS y 4481: F43B/994A/5981 por rangos GPU-owned (unreadable) y 4481 por clave nueva; fuera de la cadena solo ~1,2 ms/frame, que no se evita sin semántica. *[otro]*

Fuentes: 3d7ca311 2026-10-12, 3d7ca311 2026-10-22, commit 4a423350

<a id="materialize_stale_log"></a>
#### `MATERIALIZE_STALE_LOG` — Sonda, def. off

Razonamiento: Explicar por qué fallan la validación entradas halladas por clave. Las rancias salen de anillos por fotograma que mueven tabla y buffers por el mismo delta (un plan, 08C0, concentra 43-59 %), y los +9 fallos/frame con BLOCKS son un efecto de tiempos del frente más rápido, no evitable de forma exacta (+0,27 ms). *[otro]*

Fuentes: 3d7ca311 2026-10-12, 3d7ca311 2026-10-16, commit b5ff8694

<a id="memcopy_probe"></a>
#### `MEMCOPY_PROBE` — Sonda, def. off

Razonamiento: Problema: el hilo de render gastaba ~3,1 ms/fotograma en memmove/memset/fill/compare. Se creó la sonda para contar bytes escritos por sitio; resultado: ~0,8 ms son readbacks necesarios y lo evitable de forma exacta es solo 0,3-0,45 ms (bajo el umbral de 1 ms). Aun así se hicieron exactos y sin interruptor la caché de patrones de Fill y SkipLocalsInit (FillStackPattern 1258/s a 148/s). *[exacto-neutro]*

Fuentes: 3d7ca311 2026-10-18, commit d9102b64

<a id="obtain_probe"></a>
#### `OBTAIN_PROBE` — Sonda, def. off

Razonamiento: Problema: ~11.300 obtenciones por fotograma; hipótesis: deduplicar repeticiones exactas (15 %) o fusionar rangos (10 %) ahorraría >2 ms. Medido, ni eso ni UploadDwords (~0,13 ms) llegan al umbral, así que no se implementó nada. El primer intento dio 1,16 FPS por un fallo de la propia sonda (lista de alcance del frente sin tope). *[coste-sonda]*

Fuentes: 3d7ca311 2026-10-17, commit 1efbc6de

<a id="ooo_trace"></a>
#### `OOO_TRACE` — Sonda, def. off

Razonamiento: Hipótesis: grabar fuera de orden o solo preparar (prep-only) taparía las esperas de GPU del render (~7,2 ms dentro de ítems de cola). La traza por ítem mostró que la fuera de orden mínima gana 0,24-0,58 ms y prep-only 1,3-1,5 ms (2,85 solo con un supuesto sin comprobar), lejos de 3-4 ms; queda como rediseño del grabador. *[otro]*

Fuentes: 3d7ca311 2026-10-18, commit faf1b436

<a id="overlay_log"></a>
#### `OVERLAY_LOG` — Sonda, def. off

Razonamiento: Se creó para registrar lo que la superposición cambia en las lecturas durante la caza de ciclos BLAS; se usó en los mismos lotes que APPLY_SKIP y APPLY=0 (con LOG=1 hubo tandas con ciclos=0). *[otro]*

Fuentes: 3d7ca311 2026-10-10

<a id="pipeline_stats"></a>
#### `PIPELINE_STATS` — Sonda, def. off

Razonamiento: Se añadió junto a SCRATCH_SLOTS para leer del driver (VK_KHR_pipeline_executable_properties) registros y memoria local de cada compute. Sirvió para ver que 9FBC bajaba de 168 a 127 registros y que 8395 está en el tope de 128 con los acumuladores desbordando a memoria local (spills), de modo que ninguna variante exacta cambia los registros. *[otro]*

Fuentes: 3d7ca311 2026-10-11, 3d7ca311 2026-10-17, commit a30f43d8

<a id="profile_commit"></a>
#### `PROFILE_COMMIT` — Sonda, def. off

Razonamiento: Pieza R5 del plan estructural: separar CommitBindings y la búsqueda de pipeline en lo que no necesita la caché de imágenes (movible al hilo de replay) y lo que sí. Resultado citado después: el trabajo puro movible era solo 1,16 ms, así que mover el commit quedó descartado. *[exacto-neutro]*

Fuentes: 3d7ca311 2026-10-14, 3d7ca311 2026-10-16, commit 8f20cbf0

<a id="profile_fine"></a>
#### `PROFILE_FINE` — Sonda, def. off

Razonamiento: Las subfases finas añaden scopes en la ruta del draw (con el interruptor apagado su tiempo se queda en la fase padre). Se usa en pares FINE para comparar fases, nunca el muro perfilado: p. ej. la fase VulkanCommands de upstream (~13.000 scopes) costaba 1,62 ms/fotograma de perfil y hacía parecer peor la fusión. *[coste-sonda]*

Fuentes: 3d7ca311 2026-10-11, 3d7ca311 2026-11-04, commit 172c9fa3, commit b4e1310f

<a id="profile_flip"></a>
#### `PROFILE_FLIP` — Sonda, def. off

Razonamiento: Problema: la espera FlipDone del front era 14 ms/fotograma. La sonda la atribuyó casi toda al ritmo del reloj de display emulado (12,6-13,2 ms estática); GTA pide vsync con flip_rate 1 y 0 a 60 Hz y el pacing no es más estricto que una PS5. PRESENT_PROMPT es exacto pero no da FPS y se dejó apagado. *[exacto-neutro]*

Fuentes: 3d7ca311 2026-10-17, commit 59dea638, commit edb2d2b1

<a id="profile_front"></a>
#### `PROFILE_FRONT` — Sonda, def. 0

Razonamiento: Problema: en paneo el render esperaba 16-17 ms/fotograma en IdleBlockedCommands al front (34-38 ms de trabajo) y los contadores no distinguían sin trabajo de cabeza bloqueada. La sonda reparte el front en progreso/sin trabajo/bloqueado/contrapresión; el nivel 2 midió ProgramGate (6,1 estática, 7,7 paneo ms), que motivó PROGRAM_GATE. *[otro]*

Fuentes: 3d7ca311 2026-10-12, 3d7ca311 2026-10-23, 3d7ca311 2026-10-24, commit db5faad5, commit e1c6bc16

<a id="profile_gpu_draws"></a>
#### `PROFILE_GPU_DRAWS` — Sonda, def. off

Razonamiento: Es el interruptor de upstream de marcas GPU por comando, ampliado para GPU_PASSES. Se advirtió que las medidas llevan el sobrecoste de PROFILE_RENDER y GPU_DRAWS que un jugador no tiene, y que ab3.sh los activa por defecto (sesiones ~22 FPS frente a ~25,5 sin ellos), así que las FPS absolutas hay que medirlas sin perfil. *[coste-sonda]*

Fuentes: 3d7ca311 2026-10-11, 3d7ca311 2026-10-29, commit 82be3a52

<a id="profile_gpu_passes"></a>
#### `PROFILE_GPU_PASSES` — Sonda, def. off

Razonamiento: Se creó el 2026-10-16 para repartir los ~29,7 ms de GPU por pase, pipeline, transferencia y trabajo del emulador. Exige PROFILE_RENDER=1 (la sesión gpu1 de 2026-11-07 no sacó nada por eso); gpu2 dio GPU 36-44 ms/fotograma con dispatch 14,1 ms y barreras ~15 ms. *[otro]*

Fuentes: 3d7ca311 2026-10-16, 3d7ca311 2026-11-07, commit a72ca531

<a id="profile_gpu_run"></a>
#### `PROFILE_GPU_RUN` — Sonda, def. 0

Razonamiento: Para saber qué hay en la cola de GPU delante de las esperas largas de F43B: solo acumula los command buffers que la espera tenía por delante. Resultado: ~2,2 de ~10,1 ms por fotograma los mete el emulador, y lo más caro es el patrón de WRITE_DATA del CP; el resto es GPU del juego (la espera es GPU de la cola de delante, ~5,3 ms). *[otro]*

Fuentes: 3d7ca311 2026-10-17, commit 9298897c

<a id="profile_gpu_sites"></a>
#### `PROFILE_GPU_SITES` — Sonda, def. off

Razonamiento: Para atribuir el GPU que mete el emulador a su sitio gestionado: ~1,4 ms de 'gap_emu_shader' eran dispatches indirectos del propio juego sin marcar. Con eso corregido, el GPU del emulador es ~5 ms/fotograma en familias de ≤2 ms; la mayor son ~293 escrituras diminutas del CP (~2 ms) sin nada que fusionar, y recortarlas exacto exige un rastreador de riesgos por rango. *[otro]*

Fuentes: 3d7ca311 2026-10-16, commit 800c52d3

<a id="profile_hle"></a>
#### `PROFILE_HLE` — Sonda, def. off

Razonamiento: Problema: los hilos del juego quedaban en ~43,5 ms entre submits, con ~3.000-3.700 llamadas HLE de Agc por fotograma y hilo y ~90 % de las muestras en código del emulador (en Windows cada import pasa por el gateway gestionado). La sonda cronometra cada import para decidir los interruptores de la rama agc-hle (FAST_IMPORT_GATEWAY, escrituras en span), todos apagados. *[otro]*

Fuentes: 3d7ca311 2026-10-14, commit 8172ade0, commit ab855204

<a id="profile_lookup_gen"></a>
#### `PROFILE_LOOKUP_GEN` — Sonda, def. off

Razonamiento: Pieza R2 del plan estructural 2026-10-14: contar cuántos saltos de generación del índice de imágenes hay por fotograma y su causa (recreación D16/R16, GrowImage, GC...). No hay resultado citado en las transcripciones. *[sin-A/B]*

Fuentes: 3d7ca311 2026-10-14, commit 30ba4eb7

<a id="profile_materialize_split"></a>
#### `PROFILE_MATERIALIZE_SPLIT` — Sonda, def. off

Razonamiento: Hipótesis confirmada: los +8 ms de front que quedaban en paneo eran fallos 'grandes' que recorren la tabla de 2 MB (~520 k lecturas). El reparto señaló BigSort (+3,5 ms), y de ahí salió RECORDER_RUNSORT, que se dejó por defecto en la integración 140ea5b8. *[ganancia]*

Fuentes: 3d7ca311 2026-10-16, commit 619a69ed

<a id="profile_performance"></a>
#### `PROFILE_PERFORMANCE` — Sonda, def. off

Razonamiento: Perfil de upstream necesario para el informe de readbacks por dirección (con FRAME_TRACE); se usó para medir cuánto esperan los hilos del juego a memoria de GPU. Es caro, por eso solo se usa en sesiones de diagnóstico. *[coste-sonda]*

Fuentes: 3d7ca311 2026-10-09, 3d7ca311 2026-10-11

<a id="profile_performance_frame_trace"></a>
#### `PROFILE_PERFORMANCE_FRAME_TRACE` — Sonda, def. off

Razonamiento: Añade el desglose por dirección y fuente de las subidas y, desde db3d04a3, los contadores de acceso a memoria GPU solo del hilo de render: cerrojo de la caché de imágenes 28.800 adquisiciones y 9,05 ms retenido, cerrojo del tracker 18.400 y 6,09 ms, protección del host 3,8 ms. *[otro]*

Fuentes: 3d7ca311 2026-10-11, commit db3d04a3

<a id="profile_queues"></a>
#### `PROFILE_QUEUES` — Sonda, def. off

Razonamiento: Para decidir R3 y R7 del plan estructural: tiempo de front y back por cola del juego y, en cada espera de readback, cuánto trabajo independiente de otras colas habría para taparla (covered_ms, cota superior). Se limitó el recorrido de la cola a las esperas que superan el spin porque tomaba el cerrojo del front y lo frenaba al medir. *[coste-sonda]*

Fuentes: 3d7ca311 2026-10-14, commit 2d8a3614, commit 6e892171

<a id="profile_render"></a>
#### `PROFILE_RENDER` — Sonda, def. off

Razonamiento: Es el método de medida base (con phases.py) pero cuesta ~8 ms en el hilo de render, por lo que las FPS no valen y ab3.sh lo activa por defecto. Con RESOLVE y GPU_PASSES, que lo exigen, aporta sub-fases inclusivas y contadores de acceso a memoria GPU. *[coste-sonda]*

Fuentes: 3d7ca311 2026-10-17, 3d7ca311 2026-11-07, commit 65d0c24c

<a id="profile_resolve"></a>
#### `PROFILE_RESOLVE` — Sonda, def. off

Razonamiento: Se creó el 2026-10-16 porque el resolve era el 55 % del hilo de render (28-31 ms). Incluye el coste del propio perfilador (scope_ns, profiler_est_ms) para poder restarlo, el tiempo fuera de CPU y las materializaciones por programa (8b8615df). *[otro]*

Fuentes: 3d7ca311 2026-10-16, commit 65d0c24c, commit 8b8615df

<a id="profile_resolve_sample"></a>
#### `PROFILE_RESOLVE_SAMPLE` — Sonda, def. 8

Razonamiento: Con PROFILE_RESOLVE, solo 1 de cada N draws (8 por defecto) lee el contador de ciclos del hilo para estimar el tiempo fuera de CPU (preempción, bloqueo, pausa de GC); el muestreo evita que la lectura encarezca cada draw. *[coste-sonda]*

Fuentes: 3d7ca311 2026-10-16, commit 65d0c24c

<a id="profile_store_tables"></a>
#### `PROFILE_STORE_TABLES` — Sonda, def. off

Razonamiento: Localizó la causa de los fallos de materialización grandes en paneo: una escritura de CPU de 8 B (~20 cada 5 s) en la tabla de 2 MB borra la caché y el siguiente fallo relee la tabla entera bajo el _gate, con el render esperando. De ahí STORE_TABLE_BULK (paneo n=2: scan_ms -12,4, front ocupado -8,8). *[ganancia]*

Fuentes: 3d7ca311 2026-10-16, commit f3672abb

<a id="profile_wouldhit"></a>
#### `PROFILE_WOULDHIT` — Sonda, def. off

Razonamiento: Pieza R4: medir cuánto acertaría un memo O(1) de T# y de palabras V# normalizadas por anillo. La sonda dijo que el 96 % de los bindings de imagen acertarían, pero TARGET_MEMO y WRITTEN_OBTAIN_MEMO resultaron neutros: el acierto potencial no se tradujo en ms. *[exacto-neutro]*

Fuentes: 3d7ca311 2026-10-14, 3d7ca311 2026-10-16, commit aa71bd07

<a id="profile_yields"></a>
#### `PROFILE_YIELDS` — Sonda, def. off

Razonamiento: Instrumenta el paquete de contención: el proceso usaba ~603 % de una CPU lógica con spins del record ring y polling del guest. Cuenta usleep/yield por segundo y por hilo para saber si hay una tormenta de yields antes de activar GUEST_POLL_BACKOFF o USLEEP_SHORT_PUMP; el ahorro es 0 si no la hay. *[otro]*

Fuentes: 3d7ca311 2026-10-16, commit 80b6abbf

<a id="program_gate_verify"></a>
#### `PROGRAM_GATE_VERIFY` — Sonda, def. off

Razonamiento: Verificación de PROGRAM_GATE (búsqueda de programas sin cerrojo, que recupera los 6,1/7,7 ms de ProgramGate del front): repite cada acierto bajo la puerta y compara. =2 contra un recorrido sin caché destapó un defecto previo de scans de tablas de store obsoletos (~1403 de 10,66 M en paneo), que llevó a PROGRAM_GATE_TABLES. *[corrección]*

Fuentes: 3d7ca311 2026-10-24, commit 675fb1fd, commit e1c6bc16

<a id="program_repeat_probe"></a>
#### `PROGRAM_REPEAT_PROBE` — Sonda, def. off

Razonamiento: Hipótesis: si los draws repiten el estado de registros entero se podría hacer un memo de la preparación por draw del front (~15 ms/fotograma, ~39.900 draws/s). Resultado: ningún draw repite su estado, no hay palanca de memo, y acelerar el front solo traslada la cola al render. *[exacto-neutro]*

Fuentes: 3d7ca311 2026-10-22, commit c39fbc7d

<a id="range_dump"></a>
#### `RANGE_DUMP` — Sonda, def. off

Razonamiento: Se creó para localizar el TLAS: 8395 lo toma del qword en user data 4/5 + 0x40 y alterna dos búferes por fotograma. Corrigió la clasificación anterior: B198, D5D0, CA99, F5EB, 994A, 02F1, 11B4 y 5515 construyen el TLAS, no el BLAS; de ahí salió la idea de TLAS_HLE. *[corrección]*

Fuentes: 3d7ca311 2026-10-22, commit 5cbbd2d4, commit b16a4bef

<a id="readback_audit"></a>
#### `READBACK_AUDIT` — Sonda, def. off

Razonamiento: Halló que casi todos los readbacks de GTA son de 4-16 B y que una ventana de 512 KB descarga mucho más de lo pedido. Con READBACK_WINDOW, 4 KB duplicó los readbacks y perdió 0,9 FPS y 64 KB empató con 512 KB, así que el valor por defecto no se tocó. *[exacto-neutro]*

Fuentes: 3d7ca311 2026-10-11, commit 95fb30ad

<a id="readback_class_log"></a>
#### `READBACK_CLASS_LOG` — Sonda, def. off

Razonamiento: Clasifica los readbacks pequeños del render por el estado de su escritor para decidir si merecía el espejo READBACK_MIRROR (opt-in, diseñado a partir de Kyty). Como el espejo solo ahorraría los ~21 con escritor terminado (0,13 ms cada uno) y no los ~6 con escritor en grabación, no compensó. *[exacto-neutro]*

Fuentes: 3d7ca311 2026-10-11, commit 3f2c82f3

<a id="readback_origin_probe"></a>
#### `READBACK_ORIGIN_PROBE` — Sonda, def. off

Razonamiento: Se creó para explicar por qué TABLE_WORD_MEMO_LEARN pierde FPS al quitar la lectura síncrona de 994A: esa lectura además cortaba el lote abierto y enviaba a la GPU el refit de 5981, de modo que sin ella el trabajo llega más tarde, el juego parchea más tarde y el front espera más en REWIND (front bloqueado 2,5 a 16,9 ms). *[adelanta-cuello]*

Fuentes: 3d7ca311 2026-11-01, commit 36b59aed

<a id="readback_probe"></a>
#### `READBACK_PROBE` — Sonda, def. off

Razonamiento: Midió bytes pedidos frente a descargados: la ventana descarga unas 10.000 veces lo pedido (22 KB/s pedidos frente a ~225 MB/s; el 94 % piden ≤16 B). Bajarla de 512 a 64 KB quita 0,6 ms/fotograma de copia sin empeorar esperas, pero no llega al umbral de 1 ms y se dejó la decisión del valor. *[exacto-neutro]*

Fuentes: 3d7ca311 2026-10-22, commit 071a6b5c, commit 159c8969

<a id="readback_tick_log"></a>
#### `READBACK_TICK_LOG` — Sonda, def. off

Razonamiento: Registra el tick de escritor que espera cada pieza; se usó junto a TABLE_READ_CENSUS, PRECISE_WRITE_TICKS=2 y READBACK_CLASS_LOG en las sesiones de perfil pnq del 2026-10-29 para clasificar las esperas. No hay conclusión propia en las transcripciones. *[sin-A/B]*

Fuentes: 3d7ca311 2026-10-29

<a id="readback_wait_audit"></a>
#### `READBACK_WAIT_AUDIT` — Sonda, def. off

Razonamiento: Para ver quién espera readbacks y a quién. En rbwait halló que la mayor espera fuera de la cadena del TLAS era una dependencia falsa (el hilo de render del juego espera a la GPU para leer una etiqueta que la GPU no escribe). Clasifica en falso positivo de granularidad (PRECISE_WRITE_TICKS lo quita exacto), escritor que reescribe el mismo valor (solo HLE) y dependencia real. *[corrección]*

Fuentes: 3d7ca311 2026-10-22, 3d7ca311 2026-10-23, commit 5e674088

<a id="record_ring_check"></a>
#### `RECORD_RING_CHECK` — Sonda, def. off

Razonamiento: Añadida en la auditoría del anillo de grabación: comprueba que los VkRecord vienen del hilo de grabación y que nada llega a un búfer del anillo fuera de su grabación. La auditoría corrigió 2 carreras reales (snapshots liberados antes de que el anillo los usara y etiquetas de debug fuera del anillo) y dejó un test que busca vkCmd* fuera de VkRecord. *[estabilidad]*

Fuentes: 3d7ca311 2026-10-12, commit 0b112cb7

<a id="refit_steps"></a>
#### `REFIT_STEPS` — Sonda, def. off

Razonamiento: Diagnóstico de los refits de 5981 que giraban (rt63): contó pasos máximos y carriles cerca del guardián. Tras arreglar la pila (pops a cero 93 % a 0) el recorrido bajó de 12 s a 1,9 s por dispatch, pero el 33 % de los rayos aún llegaba a los 100 k pasos: quedaba otra causa de no terminación. *[corrección]*

Fuentes: 3d7ca311 2026-10-08

<a id="refresh_log"></a>
#### `REFRESH_LOG` — Sonda, def. off

Razonamiento: Las imágenes se recargaban enteras desde memoria del huésped; el muestreo de pila de quién las ensucia por CPU halló el memset del stencil 4K, y de ahí salió RECORD_FILL_IMAGE_CLEAR (limpieza en GPU). *[otro]*

Fuentes: 3d7ca311 2026-10-11, commit 1abf4201

<a id="region_dump"></a>
#### `REGION_DUMP` — Sonda, def. off

Razonamiento: Para comparar una ejecución rápida con una lenta se volcaron las copias de dispositivo en trozos de 1 MB a partir de un tick; así se vio que el BLAS no se construía de forma determinista. *[modo-lento]*

Fuentes: 3d7ca311 2026-10-10, commit 02d9b230

<a id="relay_probe"></a>
#### `RELAY_PROBE` — Sonda, def. off

Razonamiento: Los relays de lectura GPU->CPU costaban 15-19,5 ms/fotograma (~235 llamadas de 35-50 us); la sonda desglosa el salto complete (check, copia, rangos, páginas) para decidir si enviar la copia desde el hilo del guest (READBACK_GUEST_SUBMIT, apagado por defecto). *[otro]*

Fuentes: 3d7ca311 2026-11-07, commit 3d360738, commit fb9c406b

<a id="render2_probe"></a>
#### `RENDER2_PROBE` — Sonda, def. off

Razonamiento: Contadores de giros de espera GPU y relay usados como acompañante en los A/B de la rama render2 (VALIDATE_IN_PLACE, STORE_TABLE_FAST, MAPPED_SIZE_KEEP) para ver dónde esperaba el render. *[otro]*

Fuentes: 3d7ca311 2026-10-24, commit 10c2a18e

<a id="render_cpu_probe"></a>
#### `RENDER_CPU_PROBE` — Sonda, def. off

Razonamiento: PROFILE_RENDER inflaba ~5 ms el render, así que se midió CPU por ciclos sin instrumentar: ~30 ms/fotograma reales. Con ella se vio que TARGET_MEMO era neutro (Δ≈-0,1 ms) y que el peso real era el binding de texturas por draw y el GC (~4,5 ms). *[coste-sonda]*

Fuentes: 3d7ca311 2026-10-17, commit 033e9a67

<a id="render_wait_probe"></a>
#### `RENDER_WAIT_PROBE` — Sonda, def. off

Razonamiento: Se profundizó para ver los sitios que esperan a la GPU; sirvió para reenfocar hacia los ~7-8 ms/fotograma de espera de readbacks, al estar el resto en rendimientos decrecientes (partidas ≤2 ms). *[otro]*

Fuentes: 3d7ca311 2026-10-18, commit 9204d8d0

<a id="replay_capture"></a>
#### `REPLAY_CAPTURE` — Sonda, def. off

Razonamiento: Para investigar 0x260F se capturó el dispatch (user data, V#, páginas) y se repitió offline; cerró que la causa era del entorno, no del traductor, y un parche específico dio 22,07 FPS y GpuCompletionWait 6,3 ms (base 7,2). *[corrección]*

Fuentes: 3d7ca311 2026-10-18, commit 3027af04

<a id="replay_dir"></a>
#### `REPLAY_DIR` — Sonda, def. vacío

Razonamiento: Herramienta de la caza de 0x260F: repite offline el dispatch capturado; después se planeó reutilizarla para validar el refit de BLAS y el TLAS propuestos (~2 días de validación). *[otro]*

Fuentes: 3d7ca311 2026-10-18, 3d7ca311 2026-10-22, commit 3027af04

<a id="rq_map"></a>
#### `RQ_MAP` — Sonda, def. off

Razonamiento: Mapa de datos para evaluar ray query por hardware en 8395; la comparación halló que triángulos tipo 2/3 daban t=NaN, lo que llevó a BVH_TRIANGLE23 (8395 7,1->4,5 ms, paneo ~25,9->28,9) y dejó el RT por hardware en solo -0,6/-0,7 ms, aparcado. *[corrección]*

Fuentes: 3d7ca311 2026-10-30, commit cdadb18a

<a id="slow_mode_probe"></a>
#### `SLOW_MODE_PROBE` — Sonda, def. off

Razonamiento: Latente para cazar el modo lento (<4 FPS) pero nunca se armó en 20 sesiones, así que pasó a opt-in. BLAS_CYCLE se separó de ella porque =2 costaba ~1,4 ms por dispatch de 8395. *[modo-lento]*

Fuentes: 3d7ca311 2026-10-31, commit 70b6b3f1, commit b2f6a735

<a id="spin_blob_dump"></a>
#### `SPIN_BLOB_DUMP` — Sonda, def. off

Razonamiento: Para entender el rayo que giraba se volcó el BLAS entero y apareció un ciclo alcanzable desde la raíz; el solapado de escrituras del frente se apagó por defecto porque dejaba BLAS con ciclos en ~3 de 4 ejecuciones. *[modo-lento]*

Fuentes: 3d7ca311 2026-10-10, commit 02d9b230

<a id="srt_compile_sync"></a>
#### `SRT_COMPILE_SYNC` — Sonda, def. off

Razonamiento: Los tests ejercían casi siempre el intérprete porque el evaluador compilado se construye en segundo plano; esta opción fuerza la compilación para el oráculo diferencial. El evaluador en una pasada fue correcto pero solo quitó 0,5 ms (<1,5 de criterio): el fallo de materialización es latencia de memoria. Aparcado. *[exacto-neutro]*

Fuentes: 3d7ca311 2026-10-08, commit a8df0eea

<a id="srt_read_probe"></a>
#### `SRT_READ_PROBE` — Sonda, def. off

Razonamiento: Caracterizó qué lecturas síncronas de tabla hacen esperar al render; de las rutas exactas probadas solo TABLE_WORD_MEMO_CP_UPDATE eliminó espera de verdad y quedó activa por defecto. *[adelanta-cuello]*

Fuentes: 3d7ca311 2026-10-30, commit 8512be4e

<a id="stamp_probe"></a>
#### `STAMP_PROBE` — Sonda, def. off

Razonamiento: Contó escrituras de registros que repiten valor; motivó STAMP_ON_CHANGE. *[otro]*

Fuentes: 3d7ca311 2026-11-06, commit adfdd6e7

<a id="store_table_log"></a>
#### `STORE_TABLE_LOG` — Sonda, def. off

Razonamiento: Diagnóstico añadido junto con el apagado por defecto del solapado de escrituras del frente, que dejaba BLAS con ciclos en ~3 de 4 ejecuciones (rt340-rt367). *[modo-lento]*

Fuentes: 3d7ca311 2026-10-10, commit 57c543a0

<a id="store_table_rescan"></a>
#### `STORE_TABLE_RESCAN` — Sonda, def. 0

Razonamiento: Hipótesis: el modo lento (misma build y escena, bimodal ~9,2 vs ~8 FPS) venía de tablas de store rancias; re-escanear en cada uso dio 0/205 BLAS rotos pero a 1,77 FPS y destapó la caché nunca invalidada; =32 no cambió el modo lento. *[modo-lento]*

Fuentes: 3d7ca311 2026-10-10

<a id="syncval_selftest"></a>
#### `SYNCVAL_SELFTEST` — Sonda, def. off

Razonamiento: Comprobar que la validación de sincronización avisa de un WAW provocado, dentro del trabajo de barreras de ámbito (los draws y dispatches del emulador cuentan como escrituras sin orden). *[corrección]*

Fuentes: 3d7ca311 2026-10-17, commit c80e3d36

<a id="table_cp_probe"></a>
#### `TABLE_CP_PROBE` — Sonda, def. off

Razonamiento: Sonda del orden entre lectura de tabla y escrituras del CP para 0x260F, sin cambiar comportamiento; parte de la investigación que concluyó causa del entorno. *[otro]*

Fuentes: 3d7ca311 2026-10-18, commit ca37fb6f

<a id="table_dirty_probe"></a>
#### `TABLE_DIRTY_PROBE` — Sonda, def. off

Razonamiento: Para saber por qué una tabla quedaba GPU-modificada cada fotograma; la medición con B198 en dispositivo (F5EB) dio solo -0,3 a -0,9 ms, dentro del ruido. *[exacto-neutro]*

Fuentes: 3d7ca311 2026-10-31, commit b368c685

<a id="table_read_verify"></a>
#### `TABLE_READ_VERIFY` — Sonda, def. 0

Razonamiento: Verifica en vivo que CP_WRITE_CLEAN es exacto: 0 discrepancias en miles de comprobaciones. *[corrección]*

Fuentes: 3d7ca311 2026-11-01, commit 5fd7219d

<a id="table_word_memo_miss_probe"></a>
#### `TABLE_WORD_MEMO_MISS_PROBE` — Sonda, def. off

Razonamiento: Atribuye cada fallo del memo a quién tiró la palabra; en la rama memocp, CP_UPDATE era exacto pero no ganaba porque la espera de F43B se mudaba a B198. *[adelanta-cuello]*

Fuentes: 3d7ca311 2026-10-24, commit 173a8fe0

<a id="target_layout_verify"></a>
#### `TARGET_LAYOUT_VERIFY` — Sonda, def. off

Razonamiento: Verificación del memo de layout de targets junto a STAMP_ON_CHANGE; 0 discrepancias, pero el paquete dio solo +0,25 FPS, del orden de la desviación. *[exacto-neutro]*

Fuentes: 3d7ca311 2026-11-06, commit 8b0449ec

<a id="trace_gpu_memory_address"></a>
#### `TRACE_GPU_MEMORY_ADDRESS` — Sonda, def. off

Razonamiento: Útil como prueba barata: definirla hace que PrepareBda tome la ruta lenta que sincroniza todos los rangos; si arregla un fallo, el culpable es el barrido rápido de upstream. *[A/B-upstream]*

Fuentes: 3d7ca311 2026-11-03

<a id="upload_job_probe"></a>
#### `UPLOAD_JOB_PROBE` — Sonda, def. off

Razonamiento: Junto a PROTECT_OUTSIDE_LOCK: quitó 4 ms/fotograma de bloqueo de región en los workers de subida, pero nada estaba en el camino crítico; exacto (0 escrituras perdidas) y neutro en FPS. *[exacto-neutro]*

Fuentes: 3d7ca311 2026-11-02, commit 2e3a1f3d

<a id="vkcall_probe"></a>
#### `VKCALL_PROBE` — Sonda, def. off

Razonamiento: Cronometra la llamada nativa de los vkCmd calientes; el muestreo de pilas costaba ~16 ms/fotograma e inflaba los tiempos, así que pasó a opt-in (_CALLERS). *[coste-sonda]*

Fuentes: 3d7ca311 2026-10-12, commit b90afea4

<a id="wait_own_probe"></a>
#### `WAIT_OWN_PROBE` — Sonda, def. off

Razonamiento: Una espera ya satisfecha por el store propio de la cola no debe leer memoria: leer la etiqueta puede forzar vaciar trabajo y descargar páginas GPU. Adaptado de un cambio upstream (kuba, #1103). *[A/B-upstream]*

Fuentes: 3d7ca311 2026-10-22, commit 25f6adee

<a id="window_audit_seconds"></a>
#### `WINDOW_AUDIT_SECONDS` — Sonda, def. off

Razonamiento: Para el crash de mapeo: repite el recorrido de regiones ajenas en la ventana del guest para ver asignaciones del host tardías; acompaña a PRERESERVE_MIN en la hipótesis de gta/mapeo. *[estabilidad]*

Fuentes: 3d7ca311 2026-10-14, commit 1e9d5bda

Sin razonamiento encontrado (18): `BIND_WATCH`, `DCC_SYNC_LOG`, `GEOM_PROBE`, `LOG_GLOBAL_STORES`, `LOG_VIDEOOUT_FPS`, `LOOKBACK_PROBE`, `REFIT_PROBE`, `SHADER_SPIRV_DUMP_DIR`, `SLOT_DUMP_HASH`, `STORE_TABLE_COVER`, `SUBMIT_PROBE`, `TABLE_READ_CENSUS`, `TRACE_GPU_READS`, `UPLOAD_DUP_CHECK`, `VK_DEBUG_LABELS`, `WAIT_MEM_PROBE`, `WAIT_PROBE`, `WRITE_DIFF`.
