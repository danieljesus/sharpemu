# Resolución de draws en paralelo en mundo abierto (fase 2)

2026-11-06, rama `gta/parres` sobre `d4c3582d`. Estimaciones marcadas **[E]**; lo leído en código o logs, **[V]**.

## 0. Resumen

- **El refactor de 3–5 semanas que se diseñó en octubre ya está en el árbol: lo trajo upstream (#1128/#1130) y lo fusionamos.** Son tres piezas [V]:
  - `CommandThread` (`VulkanVideoPresenter.CommandThread.cs`): interpreta y resuelve en su propio hilo y entrega `DeferredWork` tipados (no closures) al hilo de render.
  - `AgcExports.ResolvePool` (`SHARPEMU_RESOLVE_WORKERS`, por defecto `clamp(nCPU/4,1,6)` = 4 en el 6900K): resuelve draws por delante del stream en `RenderExecutor` de trabajadores, con `ResolveReadLog` y validación al reunir en orden (`JoinDeferredDraw`, `ValidateWorkerReads`). Si algo no es seguro, lanza `SerialResolveRequiredException` y el draw se resuelve en serie.
  - `RenderWorkerPool` + slices (`VulkanVideoPresenter.Slices.cs`, `SHARPEMU_RENDER_WORKERS`, mismo defecto): graban trozos de hasta 256 ítems en `RecordingHost(worker: true)` con su arena, command buffers y `SliceRequests`. Cada uno de los ~60 sitios que mutarían cachés lanza `SerialRequiredException` y corta el segmento. El hilo de render reúne en orden (`JoinSegment`: valida imágenes y búfer, graba el prólogo con subidas y barreras, y envía).
- **Con `SHARPEMU_RENDER_PIPELINE=1`, nuestro camino, las tres piezas están muertas.** `RunCommandStreamSlices` toma `RunBackQueue` y el `CommandThread` nunca arranca. st37 lo confirma: `[PERF][SLICES] slices=0 slice_items=0` y `[PERF][RESOLVE_POOL] deferred=0` en todas las ventanas. Nadie lo ha medido en GTA: la tabla de la fusión los da como «inertes con RENDER_PIPELINE=1».
- **La fase 2, por tanto, no es diseñar un sistema desde cero, sino una de estas dos cosas:**
  - (a) hacer que nuestro frente alimente los pools de upstream;
  - (b) pasar al camino de upstream y llevarle lo nuestro.

  Antes de elegir hay que medir cuánto del trabajo por draw en mundo abierto es apto para paralelo. Este documento propone cómo medirlo, y el paso 1, ya implementado, lo mide en nuestro camino.
- **Aritmética honesta.** Aun con paralelo perfecto, el suelo serie fuera de los draws (relevo de colas, limpieza de envíos, envíos, frente) supera hoy los 33 ms. La fase 2 sola lleva el render de ~207 a ~90–110 ms/fotograma **[E]** (de 5 a 9–11 FPS). Para 30 FPS hacen falta además los recortes de la fase 1 en `QueueRelay` y en `CompletedSubmissionCleanup`, y bajar el frente (§3).

## 1. Qué cambia respecto al banco

| | Banco (oct.) | Mundo abierto (st37, 06-11) |
|---|---|---|
| Draws por fotograma | ~1.380 | ~4.400–4.600 |
| Dispatches por fotograma | ~290 | ~930–1.400 |
| Render por draw | ~18,5 µs | ~45 µs |
| Traspaso medido (`par6`) | ~9 µs/draw | (mismo mecanismo) |
| Render | ~63 ms/fotograma | ~165–207 ms/fotograma |
| Frente (trabajo útil) | ≤31 ms | 59 ms/fotograma (10 µs por encolado, 5.900 encolados) [V, st37 con perfil] |
| GPU | 30–38 ms | ~26 % ocupada |

- **En el banco**, con 9 µs de traspaso y 18,5 µs de trabajo, cada draw costaba en serie `9 + 18,5/N` µs: con N=3 quedaban 15 µs frente a 18,5. Sin ganancia: es lo que mató `par6`.
- **En mundo abierto**, con 45 µs por draw: `9 + 45/4` ≈ 20 µs frente a 45, o sea ×2,2 **[E]**. El mismo traspaso que antes perdía ahora gana, siempre que el trabajo que viaja sea el caro (resolución y preparación) y no solo la emisión de `vkCmd*`, que siguen siendo ~3 µs.
- **El veredicto de PARREC del 03-11** («la resolución debe ser serie, barrera cada ~3 draws») se midió en el banco. En mundo abierto la estructura de lotes no está medida, y es justo lo que mide el paso 1.

## 2. Reparto del render (ventana del encargo: 5,4 s, 26 fotogramas, ~207 ms/fotograma)

| Fase | ms/fotograma | Dónde iría | Notas |
|---|---|---|---|
| DescriptorPreparation | 26,7 | trabajador de slice | en `RecordingHost`; sitios `Serial` para DCC, publicación de imágenes, bindless y arena |
| PipelineCreation (consultas) | 24,9 | trabajador de slice | 138k consultas, no compilaciones; en st37 solo 2,1 % (varía por ventana) |
| QueueRelay | 22,5 | **serie** | 9.676 relevos/ventana, ~60 µs cada uno; es por envío, no por draw: fase 1 |
| DrawExecutor | 16,3 | pool de resolución o frente | `RenderExecutor`: programas, materialización, targets |
| BufferAcquisitionChecks | 16,1 | trabajador de slice | 1,3 M/ventana; el trabajador usa la instantánea `_bufferCache.Snapshot` |
| DrawResourcePreparation | 13,1 | trabajador de slice | |
| ResourceMaterialization | 12,2 | ejecutor (pool o serie) | sobre todo de dispatches; sale de `RenderExecutor` |
| CommandDispatchTranslation | 11,1 | slice si `NeedsSerialDispatch` es falso | `RenderRecorder.Compute.cs:33`: TLAS_HLE, RT_RAYQUERY, EARLY_WRITER_SUBMIT, CUT_POLICY y las sondas fuerzan serie |
| CompletedSubmissionCleanup | 9,1 | **serie** (o hilo aparte) | fase 1 o un hilo de limpieza |
| DescriptorCommit | 8,4 | trabajador de slice | push descriptors en el command buffer del trabajador |
| Resto | ~46 | mitad y mitad **[E]** | QueueSubmit, CommandStream, subidas sucias, transiciones, búsquedas… |

- **Apto para slice:** ~105 ms/fotograma **[E]**.
- **Apto para el pool de resolución:** ~20–30 ms.
- **Serie:** ~45–55 ms (relevo, limpieza, envíos, subidas en el prólogo, lecturas síncronas, ítems no draw).

## 3. Techo y orden de magnitud

- **Render tras la fase 2 sola**, en el escenario medio:
  - serie: 50 ms;
  - unión de segmentos: 8–15 ms (validar más prólogo más envío por segmento; con segmentos de ~32 draws salen ~140 por fotograma, y un envío por segmento a 30–60 µs ya son 4–8 ms);
  - resolución en serie, si no se mueve: 20 ms;
  - trabajadores en paralelo: 105 ms × 0,7 aptos / 4 ≈ 18 ms (corren a la vez que lo serie si el hilo de render no los espera).

  Resultado: **~85–110 ms/fotograma → 9–12 FPS [E]**.
- **Para 33 ms:**
  - el suelo serie tiene que bajar a ≤15 ms: `QueueRelay` de 22 a ≤4 y `CompletedSubmissionCleanup` fuera del hilo;
  - la resolución tiene que ir al pool;
  - el trabajo apto ≥85 % y N=5;
  - **el frente tiene que bajar de 59 a ≤30 ms:** es la segunda pared de Amdahl, y ninguna de estas piezas la toca.
- **Hilos:** el 6900K tiene 8C/16T. Hay que contar los hilos del juego (GTA usa las líneas `0xFF`), el frente, el render, el hilo de envío y el driver. N=4 trabajadores es realista; 6, dudoso. `HostLaneReservation.ApplyToHostWorkerThread` ya reparte las líneas.

## 4. Qué corre en N hilos y qué se queda en serie

### Paralelo
1. **Resolución por delante (`ResolvePool`).** `RenderExecutor` por trabajador, con `ResolveReadLog`. Puerta de upstream: `DrawHasTargets && DrawKnownNotToWrite`. El par de shaders tiene que haberse visto antes sin escribir memoria, y con la misma generación de `ShaderHeaderRegistry`. Al reunir: `FrontendImageVersion` igual y `ValidateWorkerReads` (versión GPU-modificada y publicaciones). Si falla, se repite en serie.
2. **Grabación de slices (`RenderWorkerPool`).** Por trabajador: `RecordingHost` propio, command buffers propios (`RecordingBuffer`), arena de subidas (`SliceArenaPool`), pools de descriptores propios (push descriptors en su buffer) y búfer de caché en instantánea inmutable (`_bufferCache.Snapshot`). Las búsquedas de imagen son de solo lectura (`TryFindImageForSlice`, `PeekImage`, `IsStaleForSlice`): un fallo es `Serial("image lookup")`, nunca una inserción. **Nada de `TouchImage`, `RememberLookup` ni creación de imágenes o búferes en un trabajador.**
3. **Descriptores, pipelines y estado dinámico** se resuelven y se graban en el trabajador. Las búsquedas de pipeline de `ShaderPipelineCache` tienen que ser seguras entre hilos para lectura. Ya lo exige upstream; revisar nuestros memos `[local]` (`STAGE_MEMO`, `ATTACH_REPEAT`, `TARGET_MEMO`), que asumen un único host.

### Serie, y cómo mantenerlo pequeño
- **Prólogo del segmento:** las subidas sucias (`SyncRanges`), las transiciones de layout que el trabajador pidió (`ImageSliceUse`), la barrera global y los bloques de oclusión, todo en orden de stream. Se mantiene pequeño porque el trabajador solo **pide** y la unión aplica en lote.
- **Ítems no draw:** escrituras de CP y etiquetas (`WriteGuest`, `PendingLabels`), fills, copias, EOP, flips, `RunAfterFlush`, `FlushFrame`, inicio de envío. Que una etiqueta corte un segmento es inevitable. Que lo corten un fill o una copia no lo es: upstream ya los lleva como «sombras» (`HasShadowBefore`) sin cortar.
- **Lecturas síncronas de memoria GPU** (readbacks, `ReadFault`): siempre en el hilo de render, en su posición.
- **`QueueRelay`, `CompletedSubmissionCleanup` y `QueueSubmit`:** por envío. Fase 1 o hilo de envío (ya existe `SUBMIT_THREAD`). En el modelo de slices, agrupar varios segmentos en un solo `FlushDeferred`.

## 5. Fronteras de lote

- **Cortes duros:**
  - ítem no draw que escribe memoria visible para el guest (etiqueta, `WRITE_DATA`, EOP);
  - lectura síncrona;
  - dispatch serie (`NeedsSerialDispatch` o `IndirectThreads`);
  - draw que no pasa la puerta;
  - cualquier `Serial(...)` del grabador.
- **No cortan en el modelo de upstream:** el cambio de render targets (el trabajador abre y cierra su propio `BeginRendering`, porque los segmentos son primarios) y los fills y copias con sombra.
- **Coste por corte:**
  - un segmento más: validación y envío;
  - más una ida y vuelta al hilo de render si el corte no es «reanudable» (`resumable: false`).
- **Con secundarios** (alternativa a primarios por segmento): un secundario por paso de render con `VK_KHR_dynamic_rendering` + `inheritanceRendering`. Así se ahorran los envíos por segmento, pero obliga a cortar en cada cambio de targets. El paso 1 mide ambas formas (corridas con y sin cambio de ámbito) para decidir.

## 6. Invariantes de corrección

1. **Orden de subidas:** toda escritura de CPU a un recurso que use un draw del segmento se aplica en el prólogo de ese segmento, antes de su envío y después de los segmentos anteriores. Los trabajadores no envían nunca.
2. **Layouts:** el trabajador graba con el layout que espera (`Expected` en `ImageSliceUse`). La unión comprueba que sigue siendo cierto y emite las transiciones; si una imagen cambió de generación (`LookupGeneration`, `MetadataGeneration`), el segmento se invalida y se graba en serie.
3. **Datos escritos por la GPU:** la resolución por delante solo vale si `GpuModifiedVersion` y `Publications` no cambiaron (`ResolveReadLog`), o si las lecturas releídas coinciden. Los draws cuyos shaders escriben memoria se quedan fuera de la puerta (`DrawKnownNotToWrite`).
4. **Readbacks y lecturas síncronas:** en serie, en su posición del stream. Ningún trabajador espera a la GPU.
5. **Etiquetas:** `PendingLabels.Add` antes de encolar la escritura tiene que seguir siendo atómico con su posición (trampa de `FrontPipeline.cs`, plan estructural del 14-10).
6. **Cachés:** los trabajadores no mutan `GuestImageCache` ni `GuestBufferCache`: ni LRU, ni memo, ni inserciones. Las mutaciones van como peticiones a la unión.
7. **Exactitud:** cada paso con verificación `=2` cuando aplique, y capturas en la misma marca (Muscle Beach, cámara fija) iguales bit a bit con el interruptor encendido y apagado.

## 6b. La forma de AnyPS5: hilo «committer» en tubería, ordenado por rangos de escritura

Fuente: estudio de solo lectura de AnyPS5 `main b5cc94b1b2` (GPL-2.0-only; solo ideas, nada de código).
- **`3b34d1fdb6`** pasa la grabación de draws a un hilo *committer* detrás del *packet worker* (`APS5_PIPELINED_DRAWS`, activo por defecto) y ordena por los rangos de memoria que escribe el guest.
- **`4651980eba`** baja la captura de memoria de shaders por draw de 15 a 6,6 µs con:
  - un memo por hilo en array sellado en vez de tabla hash;
  - registros de plan compactos de 32 B;
  - mapas de bits de 64 bits recorridos con `countr_zero`;
  - un pool de páginas por hilo.

  Con los dos commits, el interior del banco de GTA V pasa de 11,7 a ~20 FPS.
- **`ccb6697109`** da slots libres por clave en el pool de búferes (4096 por nivel). Con un tope de 512, cada draw desalojaba y recreaba búferes.
- **`a4fe5dc1c4`** añade un índice por gránulo de 64 KiB para encontrar al último escritor.

**Cómo encaja con SharpEmu:**
- **Packet worker → committer** es exactamente nuestro **frente → hilo de render** de `SHARPEMU_RENDER_PIPELINE=1`, en producción desde octubre.
  - Su «orden por rangos de escritura» equivale a:
    - nuestras validaciones al ejecutar: `FrontPreparationStillValid`, `FRONT_PREP_RANGES`, `TablesStillValid`;
    - `PendingLabels` para las etiquetas;
    - en el camino de upstream, `PendingWrites` y `ResolveReadLog`.
  - **Ese primer paso estructural ya lo tenemos.** Su ganancia en el banco es del mismo tipo que la de nuestra tubería frente/fondo, que no se ha medido aislada en mundo abierto.
- **En mundo abierto no basta:** el committer, que es nuestro render a 207 ms, es el cuello, no el intérprete. La siguiente subdivisión de la misma forma sería un **tercer hilo**: frente → **resolvedor** (ejecutor + búsquedas en caché, dueño de las cachés) → **grabador/committer** (descriptores, `vkCmd*`, envío). Es la «forma de tres etapas» de `paralelizacion-back.md` y el reparto de upstream (`CommandThread` resuelve, `RecordingHost` graba). Tope: max(resolución, grabación) en lugar de su suma. Con las cifras de §2, **[E]** ~105 ms de grabación frente a ~70 de resolución + serie, lo que da ~105 ms/fotograma. Sin trabajadores en paralelo sigue lejos de 33 ms: sirve de escalón, no de destino.
- **Dónde lo pondría en el plan:**
  - como **alternativa barata al paso 3**, si el paso 1 da corridas cortas (<8): con corridas cortas los slices no amortizan y un hilo único de grabación sí, porque no corta por lote;
  - el traspaso sería el de `par6`, pero con **todo** el trabajo de grabación por draw viajando (~25–30 µs en mundo abierto), no solo los ~3 µs de emisión.

  La sonda del paso 1 da la cifra: `draw_record_ms` es lo que se iría al committer.
- **Lo portable sin cambio estructural va a la fase 1:**
  - memo por hilo en array sellado y bitmaps con `countr_zero` (~2× en captura por draw según ellos; nuestro equivalente es `ResourceMaterialization`/`BufferAcquisitionChecks`);
  - comprobar si el pool de búferes desaloja por tope, como el que arregló `ccb6697109`. Ojo: el `n` de `ResourceDestroy` en st37 (173k por ventana) coincide con el número de draws: cuenta entradas al ámbito, no destrucciones. Hay que contar las destrucciones reales antes de perseguirlo;
  - índice de último escritor por gránulo de 64 KiB para `HasPendingBetween`/`BufferDirtySynchronization`.

## 7. Plan incremental

Cada paso va tras un `SHARPEMU_*` apagado por defecto, se mide por mecanismo (ms del hilo de render por fotograma, criterio de `sharpemu-criterio-ms`) y solo los paquetes de ≥3–5 ms se validan en FPS (6+6).

### Paso 0. Medir el camino de upstream tal cual (sin código; sesión de Daniel)
- **Cómo:** una sesión en Muscle Beach quieto, con `SHARPEMU_RENDER_PIPELINE` sin poner (o `=0`), `SHARPEMU_PROFILE_RENDER=1` y `SHARPEMU_FPS_LOG=1`. Los trabajadores salen por defecto a 4/4. Comparar con `SHARPEMU_RESOLVE_WORKERS=0 SHARPEMU_RENDER_WORKERS=0`.
- **Qué leer:**
  - `[PERF][SLICES]`: `slice_items`, `serial_items`, `serial_reasons`, `serial_cuts`, `invalid`, `join_ms`, `validate_ms`, `prologue_ms`, `worker_busy_ms` y `join_wait_ms`;
  - `[PERF][RESOLVE_POOL]`: `deferred`, `held`, `valid`, `invalid` y `serial=`.
- **Riesgo:** ese camino no se ha validado en partida desde la fusión, y nuestros interruptores del frente (`PendingLabels`, `FRONT_PREP_*`, `TABLE_WORD_MEMO` con el frente) no aplican ahí. Cuentan los contadores; el FPS es orientativo.
- **Decide:**
  - si `slice_items/(slice_items+serial_items)` ≥60 % y no hay fallos, el camino de upstream es la base y la fase 2 pasa a «portar lo nuestro al `CommandThread`»;
  - si se cuelga, rompe la imagen o da <40 %, seguimos con el frente y los pasos 2–3.

### Paso 1. Sonda de aptitud en el camino del frente (`SHARPEMU_PARRES_PROBE=1`; implementado aquí)
- **Mide, por fotograma, en el hilo de render** (detalle de las líneas en §8):
  - qué fracción de los draws y de sus ms pasaría la puerta del pool;
  - cuánto es resolución (ejecutor) y cuánto grabación;
  - la longitud de las corridas de draws aptos entre cortes, con y sin el cambio de ámbito de render como corte;
  - qué ítems cortan;
  - cuántas veces cambia la generación de búsquedas de imágenes, que invalida los segmentos.
- **Coste:** un `Stopwatch` por ítem y la puerta evaluada una vez más por draw; no cambia el comportamiento. Sesgo: con la sonda encendida, los ms suben un poco por igual en todos los brazos.
- **Seguir** si los ms aptos son ≥60 % de los ms de draw y ≥70 % de ellos caen en corridas ≥16.
- **Abandonar la fase 2 en mundo abierto** (y volver a la fase 1) si los ms aptos son <40 %, o si <40 % de ellos caen en corridas ≥8.
- **Si las corridas son cortas pero `draw_record_ms` es grande** (≥60 ms), probar antes el committer único de §6b que los slices.
- **Límite conocido:** no ve los cortes del grabador (`Serial(...)` de `RecordingHost`) ni las lecturas GPU-modificadas que haría un trabajador de resolución. Es una cota superior; el paso 0 da la cifra real del grabador.

### Paso 2. El frente alimenta el `ResolvePool` (`SHARPEMU_FRONT_RESOLVE_POOL=1`; implementado)

- **Medido antes de empezar (mb15, la sonda del paso 1):**
  - por fotograma, `draw_ms` 63,6 = resolución 9,0 + grabación 54,6, con 1.699 draws;
  - dispatches 49,1 ms, de ellos 33,3 de grabación.
- **Corrección.** El neto de 8–14 ms que preveía este paso estaba mal por ~2×. El frente ya prepara los programas de cada draw, así que en el hilo de render la resolución de draws es como mucho ~9 ms brutos, y esos 9 ms incluyen el cuerpo de la closure y la puerta de la sonda. El grueso es la grabación, que es el paso 3.
- **El valor del paso ahora:**
  - (a) la tubería que el paso 3 también necesita: draws tipados en la cola (`FrontDraw`), números de secuencia, unión en su posición y lecturas de trabajador sin `CommandThread`;
  - (b) ~~alivio del frente: los draws del pool se saltan `TryPrepareOnFront`~~. Retirado el 07-11: se saltaba la preparación de *todos* los draws, no solo de los del pool, y era el coste del brazo C (§9).
- **Cómo funciona:**
  1. El frente encola un objeto `FrontDraw` (instantánea de bancos, argumentos y registros) en lugar de la closure.
  2. Cuando un ítem ya se ha ejecutado, el hilo de render ofrece al pool los siguientes de la cola, hasta `SHARPEMU_FRONT_RESOLVE_AHEAD` (8 por defecto), mientras pasen la puerta (`DrawHasTargets && DrawKnownNotToWrite`). El recorrido se para en el primer ítem que no la pase: escritura, fill, copia, dispatch, barrera o draw que escribe memoria. Así ningún trabajador resuelve más allá de algo que cambie la memoria.
  3. En su posición, `ExecuteFrontDeferredDraw` registra el estado del draw (snapshots de geometría, targets conocidos y contadores) y llama a `JoinDeferredDraw`: espera, `ValidateWorkerReads`, y grabación o resolución en serie.
  4. Las lecturas de trabajador sin `CommandThread` leen la memoria del guest tal cual: no hay overlay ni sombras, porque nunca se pasa de una escritura en cola.
  5. Las marcas de `PendingWrites` de los draws del pool llevan secuencias crecientes y se liberan al grabarse.
- **Coste fijo:** un objeto por draw (sustituye a la closure) y un recorrido de ≤8 ítems bajo el cerrojo de la cola por ítem.
- **Abandono, reanclado:**
  - con el pool sin trabajadores (brazo C), la tubería no puede costar más de 1 ms/fotograma en el hilo de render (`draw_ms` de la sonda, A frente a C). Desde el 07-11 C ya no quita la preparación en el frente (§9);
  - con el pool encendido (brazo B), hace falta un neto ≥3 ms/fotograma frente a A, con `invalid` ≤5 % de `deferred` y `join_wait_ms` ≤3 ms/fotograma. Si no, queda solo como tubería para el paso 3, siempre que C cumpla lo anterior.
- **Brazos** (todos con `SHARPEMU_RENDER_PIPELINE=1 SHARPEMU_PARRES_PROBE=1 SHARPEMU_FPS_LOG=1`):
  - A: base;
  - B: `SHARPEMU_FRONT_RESOLVE_POOL=1` (4 trabajadores por defecto);
  - C: `SHARPEMU_FRONT_RESOLVE_POOL=1 SHARPEMU_RESOLVE_WORKERS=0`.
- **Líneas:**
  - `[PERF][PARRES]` `draw_ms`, `draw_resolve_ms`, `draw_record_ms`. En los draws del pool, `draw_resolve_ms` es la espera de la unión más la validación y el registro de estado;
  - `[PERF][RESOLVE_POOL]` `deferred`, `held`, `valid`, `invalid`, `serial=…`, `join_wait_ms`, `serial_ms`, `record_waits`. Son totales por ventana: hay que dividir por `frames` de `[PERF][PARRES]`. La sonda las imprime cuando `PROFILE_RENDER` está apagado;
  - `[PERF][FRONT]` `work_ms`/`cpu_ms`, si se pone `SHARPEMU_PROFILE_FRONT=1`.

### Paso 3. Slices bajo el frente (`SHARPEMU_FRONT_SLICES=1`; 1,5–2,5 semanas)
- **Qué:** la cola del fondo pasa a llevar ítems tipados (`CommandThread.Item` o equivalente) para draws, dispatches y barreras globales. Las closures se quedan solo para lo raro. El hilo de render corta corridas aptas en `RenderSlice` y las reparte al `RenderWorkerPool`, reutilizando `RecordSlice`/`JoinSegment`.
- **Requisitos:**
  - la tubería del paso 2 (`FrontDraw` y la unión en orden); la resolución en serie delante cuesta poco (~9 ms en mb15), así que no hace falta que el pool gane para empezar el paso 3;
  - los dispatches suponen ~45 % del tiempo de draw+dispatch en mb15 (49 ms, 33 de grabación): tienen que ir en los slices cuando `NeedsSerialDispatch` sea falso;
  - auditar los memos `[local]` del `RecordingHost` principal para el host trabajador (`ATTACH_REPEAT` ya se niega en trabajadores; revisar `STAGE_MEMO`, `TARGET_MEMO`, `TABLE_WORD_MEMO.ProgramScope` y `BindingShadow`).
- **Esperado:** grabación apta × (1−1/N) − unión. Con 70 % apto y N=4: ~105 × 0,7 × 0,75 ≈ 55 ms brutos, menos 10–15 ms de unión → **neto 40 ms/fotograma [E]**.
- **Abandonar** si:
  - el neto medido es <15 ms/fotograma;
  - la unión por segmento cuesta >40 % de lo que el segmento grabó;
  - `invalid` supera el 5 % de los segmentos;
  - hay alguna diferencia de imagen sin explicar.

### Paso 4. Suelo serie (en paralelo con los pasos 2–3; coordinar con la fase 1)
- `QueueRelay`: 370 relevos/fotograma a ~60 µs. Desglose con `SHARPEMU_RENDER2_PROBE=1` (`relay=[...]` por delegado). Objetivo: ≤4 ms.
- `CompletedSubmissionCleanup` en un hilo de limpieza: −9 ms **[E]**.
- Un solo `FlushDeferred` para varios segmentos seguidos: −2–5 ms **[E]** una vez exista el paso 3.
- **Abandonar** cada pieza si no da ≥2 ms por mecanismo.

### Paso 5. Frente (después; techo de Amdahl)
- 59 ms/fotograma y 10 µs por encolado. Candidatos:
  - instantánea de bancos copy-on-write (ya existe `banks.Snapshot` de upstream);
  - `PrepareGraphicsPrograms` al pool;
  - segundo frente para las colas de compute (R7 del 14-10).
- Se decide con `SHARPEMU_PROFILE_FRONT=1` cuando el render baje de ~60 ms.

## 8. Cómo leer la sonda del paso 1

- **Activación:** `SHARPEMU_PARRES_PROBE=1`, con `SHARPEMU_RENDER_PIPELINE=1`. Cada ~5 s escribe una línea `[PERF][PARRES]` con valores por fotograma. No necesita `PROFILE_RENDER`: mejor una sesión sin él para que el perfilador no infle los µs, y otra con él para cruzarla con las fases.

| Campo | Qué es |
|---|---|
| `items`, `draws`, `dispatches`, `other` | ítems de la cola del fondo por fotograma, y sus `*_ms` (reloj de pared del ítem en el hilo de render) |
| `draw_resolve_ms` / `draw_record_ms` | reparto del draw: todo lo que no es `RenderRecorder.Record` (ejecutor más el cuerpo de la closure: `FrontPreparationStillValid`, `TablesStillValid`, `TakeWorkerPreparation` y la propia puerta de la sonda) frente a `RenderRecorder.Record` |
| `gate=ok:N/ms,no_targets:…,unknown_pair:…,writes:…,stale_pair:…` | puerta del `ResolvePool` evaluada antes del draw: el par de shaders se vio sin escribir, nunca se vio, se vio escribiendo, o se vio con otra generación del registro de cabeceras |
| `dispatch_serial` | 1 si `NeedsSerialDispatch` (los dispatches cortan siempre) |
| `runs=` / `scope_runs=` | corridas de draws aptos: número, longitud media, y draws y ms en corridas ≥8/≥16/≥32. `scope_runs` corta además cuando cambia el ámbito de render (`DrawScope.Scope`) |
| `breaks=` | causa de cada fin de corrida: ítem no draw, dispatch, draw no apto, cambio de ámbito (solo `scope_runs`) |
| `breakers=[...]` | ítems no draw que cortaron, por método del delegado, muestreados 1/8 y con las cifras ya escaladas. Los delegados de campo salen con su nombre (`Presenter.Flush`, `FlushFrame`, `EmitGlobalBarrier`); las lambdas, como `<>c__DisplayClass…` con el método de origen entre `<>` |
| `scope_changes`, `lookup_generation_changes` | cambios de ámbito de render y de la generación de búsquedas de imagen por fotograma |

- **Sesgo:** la sonda llama a `DrawHasTargets` una vez más por draw; la llamada real acierta en el memo de layout que deja la sonda, así que en estas sesiones `[PERF][TARGET_MEMO]` sale inflado.
- **Cuentas para decidir:**
  - fracción apta = `gate ok_ms / draw_ms`;
  - techo del paso 3 ≈ `runs ms≥16 × (1−1/N)` − unión;
  - techo del paso 2 ≈ `draw_resolve_ms` de los aptos.

## 9. Paso 2 revisado (07-11): por qué C costaba ~10 µs/draw y B no sacaba la resolución del hilo de render

Medido el 06-11 en partida (ventanas de 5 s, `y2a`/`y2b`/`y2c` en `%TEMP%\gta`), cifras por fotograma de `[PERF][PARRES]`, y `[PERF][RESOLVE_POOL]` dividido por `frames` [V]:

| | draws | `draw_resolve_ms` | µs/draw | `join_wait` | `serial_ms` | serie |
|---|---|---|---|---|---|---|
| A `y2a` | 6.157 | 21,8 | 3,5 | — | — | — |
| B `y2b` (ventana de 26 fot.) | 4.216 | 19,7 | 4,7 | 6,5 | 8,5 | 638 `gpu_owned_read` (15 %) |
| B `y2b` (ventana sin serie) | 3.383 | 10,4 | 3,1 | 6,3 | 0 | 0 |
| C `y2c` | 5.846 | 77,9 | 13,3 | — | — | — |

### Brazo C: la preparación del frente desaparecía para todos los draws [V]
- `be09d9dc`, `FrontCommandStreamHost.DrawIndexed/DrawAuto`: con `FRONT_RESOLVE_POOL=1` saltaban a `EnqueueFrontDraw`, que no llamaba a `TryPrepareOnFront`, y `FrontDraw.Run` ejecutaba los no diferidos con `ExecuteQueuedDraw(..., prepared: null, ...)`. El hilo de render preparaba entonces cada draw en `RenderExecutor` (`GetGraphicsPrograms`, `RenderExecutor.cs:1024`), también los que nunca iban al pool. Con 0 trabajadores, todos.
- La diferencia C − A es 13,3 − 3,5 = **9,8 µs/draw**. La preparación del frente cuesta eso mismo: en `gcMa1` (`[PERF][FRONT_PHASES]`, 150 fotogramas, 155k draws), ProgramInputs + Source + Lookup + Tail + Vertex/PixelInputs + SourceIdentity + ProgramKey + Materialize + Permutation suman ~1,6–1,9 s, es decir **10–12 µs por draw**.
- No era la tubería: con 0 trabajadores, `TryDeferFrontDraw` sale en la primera comprobación, y el recorrido es un cerrojo y un elemento por ítem.

### Brazo B: el hilo de render esperaba y repetía [V]
- La resolución sí salía del hilo para ~99 % de los draws (`deferred` ≈ draws, `invalid=0`). Lo que quedaba en `draw_resolve_ms` era: 6,5 ms de `join_wait` + 8,5 ms de resolución en serie + ~4,7 ms (1,1 µs/draw) de registro de estado, validación y unión.
- **Serie:** el 15 % de los diferidos volvía con `gpu_owned_read` (`ReadOnResolveWorker`, `VulkanVideoPresenter.CommandStream.cs:359`). El hilo de render primero esperaba a que el trabajador fallara y luego resolvía `ResolveOnThisThread` **sin** programas preparados: 8,5 ms / 638 = 13,3 µs, el coste del brazo C.
- **Espera:** el trabajador también resolvía sin preparación del frente (~13 µs por draw). El recorrido se para en cada ítem que no es draw apto y solo se reanuda cuando ese ítem ha corrido, así que el primer draw de cada corrida (~190–200 corridas por fotograma, `runs n`) se ofrecía sin ninguna ventaja. Para ese draw, la unión esperaba despertar + 13 µs: 200 × ~30 µs ≈ 6 ms. Además, `Dispatch` encaminaba por par de shaders y solo desbordaba con `>32` en cola, algo que la ventana de 8 nunca alcanza. Las 8 de una corrida del mismo par iban a un solo trabajador.
- **Coste oculto:** `OfferAhead` corre después de `EndItem`, así que su tiempo no cae en ningún cubo de la sonda. Por draw diferido, en el hilo de render, incluía una copia completa de `RegisterBanks` (`banks.Snapshot`, ~1,3 µs según `FRONT_PHASES` Snapshot 262 ms / 198k), el hash del par y `BlockingCollection.Add` (`SemaphoreSlim.Release`, que despierta al trabajador si duerme).

### Cambios (commit de este paso; todo tras `FRONT_RESOLVE_POOL`, apagado por defecto)
1. **El frente prepara siempre**, como sin el pool (`EnqueueFrontDraw`). `FrontDraw` lleva `Prepared`, `Inputs`, `version`, `PrepPages` y `TableMark`, y `Run` repite en su posición las comprobaciones de la closure en línea (`FrontPreparationStillValid`, `TablesStillValid`, `VerifyGraphics`).
2. **El trabajador resuelve con esa preparación** (`DeferredDraw.Prepared`, sobrecargas `DrawIndexed/DrawAuto(..., prepared, inputs)`). Si la posición la descarta, `JoinDeferredDraw(preparationHeld: false)` resuelve otra vez en orden con lo que valga allí (`front_prep_discarded`).
3. **`ResolveOnThisThread` usa la preparación del frente** cuando sigue valiendo, así que una resolución en serie cuesta ~3,5 µs y no ~13.
4. **En línea, sin pasar por el pool**, y el recorrido sigue detrás de ellos:
   - el draw que corre a continuación (`next`: nada ofrecido delante; `front_inline_next`);
   - los pares que acaban de volver con motivo serie, durante los 64 draws siguientes del par (`FrontSerialPairSkip`; `front_inline_serial_pair`).

   Seguir detrás de un draw en línea que no escribe memoria descansa en la misma suposición que ya hacía el pool entre diferidos: sus marcas son solo de render targets.
5. **El diferido toma prestada la instantánea del frente** (`Banks = snapshot`): ninguna copia en el hilo de render. La instantánea vuelve a la pila del frente en el `finally` de `Run`, después de la unión, y los diferidos del frente tienen su propia reserva (`_spareFrontDeferred`).
6. **El pool del frente desborda con 2 en cola** en lugar de 32 (`FrontSpillQueueLength`).
7. **El recorrido rellena** cuando ha corrido la mitad de lo ofrecido, no tras cada ítem: el salto inicial es O(ofrecidos).
8. **Sonda:**
   - `[PERF][PARRES]` gana `lookahead_ms` (el recorrido entero, fuera de los ítems), `slice_runs` y `draw_barrier_runs` (§10);
   - `[PERF][RESOLVE_POOL]` gana `front_inline_next`, `front_inline_serial_pair` y `front_prep_discarded`.

### Esperado y cómo medirlo
- **C** ≈ A + `lookahead_ms` (un cerrojo y una puerta por ítem) **[E] ≤0,5 ms/fotograma**. El frente vuelve a costar lo de A: ya no se ahorra la preparación.
- **B**, por draw diferido, en el hilo de render: ~1,1 µs (estado + validación + unión) frente a 3,5 en línea, o sea −2,4 µs × ~3.800 ≈ **−9 ms [E]**. Hay que restar `lookahead_ms` (hash + `Add`/despertar por diferido: 0,5–2 µs × 3.800 = 2–8 ms **[E]**, la incógnita) y la espera que quede (~1 ms). El neto va de **0 a 7 ms [E]**: hay que medirlo.
  - Si `lookahead_ms` se come la ganancia, la siguiente palanca es la entrega: una señal por recorrido y trabajador en vez de por draw, o trabajadores que giran un poco antes de dormir.
- **Brazos** (todos con `SHARPEMU_RENDER_PIPELINE=1 SHARPEMU_PARRES_PROBE=1 SHARPEMU_FPS_LOG=1`):
  - A: base;
  - B: `SHARPEMU_FRONT_RESOLVE_POOL=1` (4 trabajadores);
  - B16 y B32: B con `SHARPEMU_FRONT_RESOLVE_AHEAD=16` o `32`;
  - C: `SHARPEMU_FRONT_RESOLVE_POOL=1 SHARPEMU_RESOLVE_WORKERS=0`.
- **Cuentas:**
  - hilo de render en draws = `draw_ms + lookahead_ms` (frente a `draw_ms` de A);
  - puertas: C − A ≤ 1 ms; A − B ≥ 3 ms con `join_wait_ms/frames` ≤ 3;
  - `front_inline_next` debería rondar `runs n` × `frames`;
  - si `front_inline_serial_pair` es grande y `serial=` pequeño, la memoria de pares funciona.

## 10. Paso 3: diseño de `SHARPEMU_FRONT_SLICES` (grabación en paralelo)

### Lo que hay que mover [V, `y2a`]
- Por fotograma (cifras del encargo, 4.458 draws): `draw_record` 84,7 ms y `dispatch_record` 55,6 ms. Son **~140 ms de grabación** frente a 16,2 + 22,6 = ~39 ms de resolución, sobre ~176 ms de hilo de render.
- **Qué corta hoy las corridas** (`breakers`, última ventana): `EmitGlobalBarrier` 156/fotograma y `WriteGuest` 27. El resto es despreciable.
  - Upstream graba la barrera global **dentro** del slice (`GlobalBarrierWork` → `RecordGuestCacheFlush`) y no corta. Con la barrera tipada, solo cortaría `WriteGuest` (y las etiquetas y lecturas síncronas que lleve).
  - Las corridas medias pasarían de 31 draws a varios cientos de ítems **[E]**. Los campos nuevos `slice_runs` (draws + dispatches + barreras; un dispatch corta solo si `NeedsSerialDispatch`) y `draw_barrier_runs` (draws + barreras; un dispatch corta) dan la cifra en la próxima sesión, sin código nuevo.
- `scope_changes` ≈ 444 por fotograma de la sonda: cortar en cada cambio de ámbito (secundarios con `RENDER_PASS_CONTINUE`) daría corridas de ~14. **No es la forma.**

### Forma
1. **Ítems tipados en la cola del fondo:**
   - `FrontDraw` (existe);
   - `FrontDispatch` (nuevo, con la misma comprobación de la preparación del frente);
   - la barrera: el delegado `_barrier` es una instancia única, así que se reconoce por referencia, como en la sonda.

   El resto de ítems siguen siendo closures, y cada una corta el slice.
2. **El hilo de render resuelve en orden y deja de grabar.** Cada ítem tipado de la corrida se resuelve con el ejecutor y un sumidero que captura el registro (como `CapturingSink`), con secuencia `++_frontSequence`, o se toma del pool del paso 2 cuando lo haya. El resultado se añade a un `RenderSlice` como `DeferredWork` de upstream (`ResolvedDrawWork`, `ResolvedDispatchWork`, `GlobalBarrierWork`).
3. **Cierre y reparto:** el slice se cierra a 64 ítems o en un corte, y se reparte al `RenderWorkerPool` de upstream (`RecordingHost(worker: true)`, `RecordSlice`).
4. **Unión:** antes de ejecutar el ítem que corta, el hilo de render une en orden con `NextSegment`/`JoinSegment` de upstream (validación, prólogo de subidas y transiciones, `FlushDeferred`). Con `RunInline` si el segmento salió `Serial(...)` o no valida.
5. **Tubería:** el hilo de render resuelve el slice k+1 mientras los trabajadores graban el k, con hasta 2×N slices por delante (como `FillUnits`), y une el más viejo en cuanto termina.

### Búferes de comandos
- **3a: primarios por segmento.** El código de upstream, sin cambios en Vulkan. Coste: un `FlushDeferred` por segmento.
- **3d: secundarios que abren su propio `vkCmdBeginRendering`.** Sin `RENDER_PASS_CONTINUE`, así que no hace falta cortar en cada cambio de ámbito. El hilo de render los ejecuta con `vkCmdExecuteCommands` en su búfer de lote, detrás del prólogo. Se ahorra el envío por segmento, y el orden queda dentro de un solo primario.
  - Dentro del secundario valen las barreras fuera de la instancia de render y las consultas de oclusión que empiezan y terminan en él.
  - Pool de comandos por trabajador (`VkCommandPool` se sincroniza por fuera).
  - Solo merece la pena si `join_ms` de 3a supera ~20 % de lo grabado.

### Riesgos
1. **Orden de lecturas frente a escrituras de la GPU:** un registro resuelto antes de que lo anterior esté grabado puede leer un rango que escribe un dispatch aún sin unir. Ejemplos: argumentos indirectos (D9800) o palabras de tabla (994A).
   - Con secuencias reales en `PendingWrites` (las del paso 2), la resolución en el hilo de render tiene que comprobar `HasPendingBetween(Released, seq)` mientras haya slices sin unir. Si acierta: cortar, unir todo lo pendiente y resolver en línea. Hoy esa comprobación no existe en el camino del hilo de render, porque con `Producing` falso las marcas llevan secuencia 0.
   - Etiquetas, `WriteGuest`, lecturas síncronas y readbacks son cortes duros y exigen todo unido y enviado.
2. **Barreras:**
   - `RecordGuestCacheFlush` del trabajador usa `BatchBarriers`/`DeferGlobalBarriers`;
   - nuestro `SCOPE_BARRIER` (mode=2, poda ~6.600 de ~8.800 ámbitos por ventana de 5 s) vive en el host principal: o va por host, o la unión aplica la poda;
   - cada segmento empieza con `GlobalMemoryBarrier` (`BeginSegment`). Es correcto, pero cuesta barreras en GPU: con 64 ítems son ~110 por fotograma, frente a ~600 barreras completas hoy (`full_barriers`=12.353 por ventana de ~20 fotogramas en `SCOPE_BARRIER`).
3. **Cortes de ámbito:** `EndSegment` cierra el render. Cada segmento vuelve a abrir sus adjuntos (LOAD) y pierde la fusión de ámbitos y `ATTACH_REPEAT` (que ya se niega en trabajadores). Es coste de GPU; se mide con `[PERF][SCOPE_BARRIER]` y el tiempo de GPU.
4. **Descriptores y seguridad entre hilos:**
   - los push descriptors van en el búfer del trabajador, y cada `RecordingHost(worker)` tiene sus pools;
   - el bindless y la arena son sitios `Serial` en upstream;
   - nuestros memos `[local]` del host principal se auditan uno a uno, y o pasan a ser por host o se niegan en el trabajador: `STAGE_MEMO`, `TARGET_MEMO`, `TABLE_WORD_MEMO.ProgramScope`, `BindingShadow`, `DA_ZERO_PAGE_BRANCH`, `ATTACH_REPEAT` (ya negado), `RECORD_FILL`, `PRECISE_WRITE_TICKS`;
   - `SubmissionScheduler.MarkReadOnlyThread` ya convierte en `Serial("unguarded …")` toda operación sin guarda.
5. **Cachés:** imagen o búfer no encontrado = `Serial(..., resumable)`. En fotogramas calientes debería ser raro, pero nadie lo ha medido en GTA (el paso 0 sigue pendiente). Es la incógnita grande.
6. **La resolución sigue en el hilo de render** (~39 ms) mientras el paso 2 no gane.

### Esperado [E]
- **Grabación que se va a los trabajadores:**
  - suponiendo 85 % apta en corridas ≥16 (a confirmar con `slice_runs`), son ~119 de los 140 ms;
  - con N=4, ~30 ms de pared, solapados con la resolución.
- **Unión con primarios:** ~40 µs por segmento (validación + prólogo + `FlushDeferred`) × ~150 segmentos ≈ **6 ms**. Con secundarios, ~2 ms.
- **Cortes `Serial(...)` del grabador:** desconocidos. Si el 15 % de lo grabado vuelve en línea, son **+18 ms**.
- **Hilo de render:** 176 − 119 + 6 + 18 ≈ **80 ms** (~12 FPS); con el pool del paso 2 y secundarios, ~65 ms. Bajar a 50 ms (20 FPS) exige además el suelo serie del paso 4 (`QueueRelay`, limpieza).

### Primer incremento mínimo (3a, ~3–4 días)
- **Alcance:**
  - solo draws aptos y barreras tipadas;
  - un dispatch o cualquier otro ítem corta, así que dentro de un slice no hay riesgo de lectura pendiente: los draws aptos no escriben memoria, la misma suposición que el paso 2;
  - la resolución de la corrida entera (hasta 64 ítems) en el hilo de render, a `ResolvedDrawWork` con la preparación del frente;
  - las corridas ≥32 se parten en `min(N, len/16)` slices para que graben a la vez;
  - unión en orden antes del ítem que corta, sin tubería entre corridas;
  - primarios por segmento: el código de upstream tal cual.
- **Interruptor:** `SHARPEMU_FRONT_SLICES=1` (exige `RENDER_PIPELINE=1`), con N = `SHARPEMU_RENDER_WORKERS`. Hay que arrancar `CreateRenderWorkers` en el camino del frente, que hoy no lo hace.
- **Antes de escribirlo:** una sesión A con la sonda nueva. Seguir si `draw_barrier_runs` en ≥16 da ≥60 ms/fotograma. Si da menos, pero `slice_runs` en ≥16 sí los da, saltar directamente a 3b.
- **Puertas:**
  - neto en el hilo de render ≥10 ms/fotograma;
  - validación + prólogo + envío ≤30 % de los ms grabados;
  - segmentos inválidos ≤5 %;
  - capturas iguales bit a bit en la marca fija.
- **Después:**
  - 3b: dispatches dentro de los slices, con la regla de corte por `PendingWrites`;
  - 3c: tubería de K slices por delante;
  - 3d: secundarios.

### 3a implementado (07-11, `SHARPEMU_FRONT_SLICES=1`)
- **Código:**
  - `FrontSliceCutter.cs`: el corte y la unión, genéricos y con pruebas (`FrontSliceCutterTests`). Cierra a `SHARPEMU_FRONT_SLICE_ITEMS` (64) y reparte al momento. En un corte, el resto se parte en `min(N, len/16)` (uno por debajo de 32; ninguno por debajo de `SHARPEMU_FRONT_SLICE_MIN`, 8, que corre en el hilo de render). Une en orden como `CommandThread.JoinSlice`: robado, segmento inválido o vacío, el ítem `Serial(...)` y la cola tras `Resume` corren en línea.
  - `VulkanVideoPresenter.FrontSlices.cs`: el puente con el `BackQueue` (`ISlicer`) y con `RenderWorkerPool`/`NextSegment`/`JoinSegment`/`FinishSlice` de upstream, sin cambios en estos.
  - Captura en la traducción (`BeginSliceCapture`): el draw corre igual que en serie (`FrontDraw.Run` → `ExecuteQueuedDraw` → ejecutor), pero `ExecutorSink` guarda el registro en vez de grabarlo y le da secuencia (`++_frontSequence`), así que sus escrituras pendientes (render targets) quedan marcadas hasta la unión.
- **Qué se toma:** draws del frente que pasan la puerta del pool (`DrawPassesSliceGate`) y cuyo registro es `Draw`/`Skipped` sin `WritesMemory`, y las barreras globales con algo tomado. Cualquier otro ítem une todo antes de correr.
- **Cortes y vueltas a serie (contados en `slice_flushes`/`slice_inline`):**
  - `item`: ítem no tomado;
  - `exit`: fin de `RunPending` (sin tubería entre llamadas);
  - `read`: una lectura de memoria del guest en el hilo de render (`TryReadGuest`, `DrainingWriteMemory`) que solapa una escritura pendiente de un draw tomado;
  - `record`: registro que no es un draw simple o que escribe memoria;
  - `after`: antes de `AfterItem` (`RUNAHEAD_TICKS`).
- **Seguridad:**
  - el banco del frente que lee el registro (`Context`) vuelve a la pila solo tras la unión (`HoldSnapshot`);
  - sin hilo de comandos, el trabajador no hace copias de flujo (`stream_fallbacks`);
  - `SHARPEMU_FRONT_RESOLVE_POOL` se ignora con slices.
- **Sonda** (`[PERF][PARRES]`, por fotograma):
  - `front_slices=n,items_mean,items,joined,segments`;
  - `slice_handoff_ms`, `slice_join_wait_ms` (con los relevos que corran en la espera), `slice_join_ms`, `slice_inline_ms`, `slice_worker_ms`;
  - `slice_inline=short,stolen,invalid,serial,tail`, `slice_flushes=item,exit,read,record,after` y `slice_serial=[motivo:n]`.
  - Con slices, `draw_record_ms` solo cuenta lo que se graba por el sumidero del ejecutor, y `draw_ms` incluye las uniones `read` que caen dentro de un draw.
- **Brazos** (con `SHARPEMU_RENDER_PIPELINE=1 SHARPEMU_PARRES_PROBE=1 SHARPEMU_FPS_LOG=1`): A base; S `SHARPEMU_FRONT_SLICES=1` (N = `SHARPEMU_RENDER_WORKERS`; por defecto, procesadores/4 entre 1 y 6; desde el 07-11 tarde el resto de un corte por debajo de `SHARPEMU_FRONT_SLICE_MIN`=32 corre en línea, =8 da el reparto original); S0 = S con `SHARPEMU_RENDER_WORKERS=0`, que debe dar lo mismo que A (nada se toma).

### 3a medido y 3b implementado (07-11 noche)
- **3a medido** (`sca`/`sc1`/`sc6`, por fotograma) [V]: neutro. Hilo de render en draws 82,3 ms (A) frente a 79,4 (S, 4 trabajadores) y 83,5 (6). El trabajador cuesta ~26–28 µs por ítem unido con 4 y con 6 (no es contención). En `sc1`:
  - `segments` 326 ≈ `front_slices n` 100 + `serial` 246: cada corte `Serial(resumable)` termina un segmento y abre otro;
  - `depth_target_acquire` 200,6 = `lookup_fail depth_stencil` 5.616 / 28 fotogramas: todas las negativas de profundidad son la comparación de `Stencil` en `TryPrepareDepthTargetViewForSlice`;
  - `image_lookup` 38 = (`dcc_clear` 1.043 + `no_candidate` 20) / 28: todas son `DccClearMaterializationIsNoop`, rol color;
  - `slice_inline` 1.662 ítems (31 ms): `short` 1.014 (restos < 32 entre ~199 cortes), `stolen` 295 (restos que el trabajador aún no había empezado), `serial` 246, `tail` 108. `join_wait` 28 ms es la espera por el resto de cada corte;
  - el cuello no es el coste por ítem del trabajador sino los cortes: ~199/fotograma por dispatches no tomados (`dispatch_record_ms` 65,7 sigue en el hilo de render).
- **Profundidad con stencil** (`SHARPEMU_SLICE_STENCIL_PLANE=1`, apagado por defecto): `AcquireDepthTargetCore` guarda `StencilPlaneOfImage(imagen, petición)`, que para una vista de menos capas que la imagen ensancha el plano a todas las capas. El trabajador comparaba con el tramo de la petición, que en ese caso nunca coincide. Ahora compara con el plano que dejaría la adquisición en serie: si coinciden, esa asignación no cambia nada (exacto). `lookup_detail=stencil_unset|stencil_layers|stencil_address` dice qué caso era, y `stencil_plane_matched` cuántas acepta el cambio.
- **DCC** (`SHARPEMU_SLICE_DCC_NATIVE=1`, apagado): `MaterializeDccClear` vuelve sin tocar nada con `NativeColorClear`; el trabajador exigía además metadatos iguales. `lookup_detail=dcc_metadata|dcc_metadata_native|dcc_layout|dcc_dirty|dcc_read|dcc_key` separa las negativas; `dcc_dirty` (lectura de GPU) y `dcc_key` (borrado pendiente) son serie de verdad.
- **Sonda del trabajador** (siempre con `PARRES_PROBE`): `slice_worker=segments:n/begin_us/end_us,items:n/us,dispatches:n/us,serial:n/us,scopes:n,attach:n/us,kept:n,lookups:n/us` y `slice_inline_us` (coste por ítem en línea del hilo de render).
- **3b** (`SHARPEMU_FRONT_SLICE_DISPATCHES=1`, exige `FRONT_SLICES=1`; apagado):
  - los dispatches directos del frente van en un `FrontDispatch` (lo mismo que la closure) y se toman salvo `NeedsSerialDispatch`. Su registro se captura (`ResolvedDispatchWork`) con secuencia propia, así que `MarkPendingWrites` marca sus escrituras hasta la unión. Lo graba un trabajador con el código de upstream (`Serial` para lo que necesita el hilo de render: rellenos, copias de imagen, indirectos por hilos);
  - lecturas del hilo de render que solapan una escritura pendiente unen antes: además de `TryReadGuest` y la memoria de comandos, ahora `TryReadGuestWord` (palabras de tabla del materializador, antes de la memoria de `TABLE_WORD_MEMO`), `TryReadCleanGuestWord` y `TryReadResidentGuestBytes`;
  - preparación del frente: la versión de rangos escritos por primera vez solo cambia al unir. Con un dispatch tomado abierto, una preparación que leyó una página con escritura pendiente, o cuyas páginas no se conocen, une antes de `FrontPreparationStillValid` (corte `prep`). Así la comprobación ve lo mismo que en serie;
  - `TABLE_WORD_MEMO`: la unión aplica cada uso escrito como escritura del programa que lo preparó en el trabajador (`BufferSliceUse.Program`), igual que en serie; si no, el refit 5981 dejaría de estar en la lista blanca;
  - un draw o dispatch con *call records* no se toma (corte `record`): sus rangos de dirección de dispositivo solo los encuentra el host al grabarlo (`PrepareCallRecords`; el trabajador lo devuelve con `Serial`), así que sus escrituras no están entre las pendientes. Vale también para 3a;
  - una unión desde una lectura dentro de una puerta del caché de pipelines (`_gate`, `_pipelineGate`) esperaría a un trabajador que la necesita: es un `Fatal` explícito y no un bloqueo;
  - el orden en los búferes de comandos es el del flujo, segmento a segmento. Pruebas: `FrontSliceCutterTests` (mezcla con `Serial`, robado, `tail` y corte `prep`; `FrontSliceGuards`).
- **Esperado [E]:**
  - 3a + profundidad: −200 segmentos y −200 ítems en línea por fotograma, ~−4 a −6 ms;
  - 3b: los cortes pasan de ~199 por dispatches a los ~364 ítems «otros» de `slice_runs`. Pero el 94 % de los ítems está en corridas ≥32 (6.022 de 6.395 en `sc1`), y la grabación de dispatches (65,7 ms) sale del hilo de render. Si los dispatches no vuelven en serie en masa, el hilo de render en draws+dispatches baja de ~160 a 60–90 ms;
  - la incógnita son los `Serial` no reanudables de dispatches (`dcc fill`, `image clear`, `frontend image copy`), que mandan el resto del slice a `tail`: leer `slice_serial` y `slice_inline=tail`.
- **Brazos** (acumulativos, con `SHARPEMU_RENDER_PIPELINE=1 SHARPEMU_PARRES_PROBE=1 SHARPEMU_FPS_LOG=1 SHARPEMU_FRONT_SLICES=1`):
  - S: 3a tal cual (la sonda nueva y `lookup_detail` ya salen);
  - SD: S + `SHARPEMU_SLICE_STENCIL_PLANE=1`;
  - SDN: SD + `SHARPEMU_SLICE_DCC_NATIVE=1` (solo si `lookup_detail` muestra `dcc_metadata_native`);
  - SB: SD + `SHARPEMU_FRONT_SLICE_DISPATCHES=1`;
  - SB6: SB con `SHARPEMU_RENDER_WORKERS=6`.
