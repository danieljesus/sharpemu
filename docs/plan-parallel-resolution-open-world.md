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
  - (b) alivio del frente: los draws del pool se saltan `TryPrepareOnFront`.
- **Cómo funciona:**
  1. El frente encola un objeto `FrontDraw` (instantánea de bancos, argumentos y registros) en lugar de la closure.
  2. Cuando un ítem ya se ha ejecutado, el hilo de render ofrece al pool los siguientes de la cola, hasta `SHARPEMU_FRONT_RESOLVE_AHEAD` (8 por defecto), mientras pasen la puerta (`DrawHasTargets && DrawKnownNotToWrite`). El recorrido se para en el primer ítem que no la pase: escritura, fill, copia, dispatch, barrera o draw que escribe memoria. Así ningún trabajador resuelve más allá de algo que cambie la memoria.
  3. En su posición, `ExecuteFrontDeferredDraw` registra el estado del draw (snapshots de geometría, targets conocidos y contadores) y llama a `JoinDeferredDraw`: espera, `ValidateWorkerReads`, y grabación o resolución en serie.
  4. Las lecturas de trabajador sin `CommandThread` leen la memoria del guest tal cual: no hay overlay ni sombras, porque nunca se pasa de una escritura en cola.
  5. Las marcas de `PendingWrites` de los draws del pool llevan secuencias crecientes y se liberan al grabarse.
- **Coste fijo:** un objeto por draw (sustituye a la closure) y un recorrido de ≤8 ítems bajo el cerrojo de la cola por ítem.
- **Abandono, reanclado:**
  - con el pool sin trabajadores (brazo C), la tubería no puede costar más de 1 ms/fotograma en el hilo de render (`draw_ms` de la sonda, A frente a C). Ojo: C además quita la preparación en el frente, así que mezcla las dos cosas;
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
