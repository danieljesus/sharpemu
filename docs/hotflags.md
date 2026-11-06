<!--
Copyright (C) 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later
-->

# Interruptores en caliente (`SHARPEMU_HOTFLAGS_FILE`)

Permite cambiar unos cuantos interruptores `SHARPEMU_*` con el emulador en marcha, para alternar A-B-A-B cada ~30 s
en la misma escena sin recargar la partida.

## Activación

```
SHARPEMU_HOTFLAGS_FILE=C:\ruta\hot.txt
```

Sin la variable no se mira ningún fichero: el coste es un incremento del contador de presentaciones y una rama sobre
un `static readonly` por fotograma presentado. Cada interruptor arranca con el valor de su propia variable, igual que
antes; el fichero solo cambia valores a partir de ahí.

Con la variable, el hilo de render, tras cada presentación y como mucho cada 250 ms, comprueba la fecha de escritura y
el tamaño del fichero (`FileInfo.Refresh`, unos µs) y solo lo vuelve a leer si alguno cambió. Si el fichero no existe
o no se puede leer, no pasa nada y se reintenta en la siguiente consulta.

## Formato

```
# comentario
SHARPEMU_IDLE_FLUSH=0
STAGE_MEMO=1   # con o sin el prefijo SHARPEMU_
```

- Una línea `NOMBRE=valor` por interruptor; sin distinguir mayúsculas; `#` comenta hasta el final de la línea.
- Si un nombre se repite, gana la última línea.
- Solo se aplica una línea cuyo valor cambió desde la última lectura; quitar una línea **no** devuelve el valor de
  arranque (para volver, escribe el valor viejo).
- Valores enteros dentro del rango del interruptor; `SUBMIT_BATCH` e `IDLE_FLUSH` se recortan a su rango como su
  variable.

Mensajes (a stderr, es decir, run.log):

```
[HOTFLAG] watching C:\ruta\hot.txt frame=1 STAMP_ON_CHANGE=0 BUFFER_SOURCE_MEMO=0 ...   (valores de arranque)
[HOTFLAG] IDLE_FLUSH=4->0 frame=5321
[HOTFLAG] rejected DRAWS_PER_BATCH (not hot-safe)
[HOTFLAG] rejected STAGE_MEMO=7 (invalid, expected 0..1)
```

`frame` es el número de fotogramas presentados desde el arranque (`HotFlags.PresentedFrames`, contado en
`FpsLog.FramePresented`, la misma llamada por presentación que alimenta `[PERF][RENDER_MS]`).

## Interruptores en caliente

| Interruptor | Valores | Al cambiar |
|---|---|---|
| `STAMP_ON_CHANGE` | 0/1 | **Reinicia**: cada banco de registros olvida sus sombras crudas en su siguiente escritura comparada (en el hilo que lo escribe). Sin esto, al encenderlo se compararía contra sombras de antes de apagarlo. |
| `BUFFER_SOURCE_MEMO` | 0/1/2 | Tal cual: cada acierto de la tabla se comprueba (mapping, spans, region stamp) igual que siempre. Leído una vez por etapa. |
| `NATIVE_STATE_REUSE` | 0/1/2 | **Reinicia**: cada RecordingHost reinicia su sombra en el siguiente bind (generación comparada en `SyncNativeShadow`). Evita que un bind grabado justo en el cambio deje la época aceptada con una sombra sin actualizar. |
| `FRONT_SPECULATE` | 0/1/2 | Tal cual: Begin/End/Take siguen el estado de cada preparación. El despacho del front lee el modo una vez y lo pasa a la preparación. |
| `SUBMIT_BATCH` | 0..32 | Tal cual: el hilo de envío lo lee una vez por lote. Un scheduler con límite propio (tests) no lo sigue. |
| `TEXEL_CLEAN_SKIP` | 0/1/2 | Tal cual: leído una vez por sincronización de búfer. |
| `IDLE_FLUSH` | ≥0 | Tal cual: leído una vez por preparación. |
| `STAGE_MEMO` | 0/1 | **Reinicia**: cada host de grabación invalida y olvida sus memos de etapa en su siguiente preparación de etapa. Mientras está apagado no se apuntan memos nuevos y las invalidaciones por binding dejan de alcanzar los viejos. |
| `TARGET_MEMO_DCC` | 0/1 | Tal cual: el memo de targets se valida por el stamp de búsqueda y las palabras del target. |
| `ASYNC_GRAPHICS_PIPELINES` | 0/1/2 | Tal cual, leído una vez por búsqueda de pipeline. Un pipeline que se está compilando en un worker al pasar a 0 se vuelve a crear síncrono; la copia del worker queda sin usar hasta el cierre (uno por compilación en vuelo). |

Las lecturas en los caminos calientes son una carga de un campo `static volatile` (en x64, un `mov`); lo único que se
pierde es que el JIT ya no pliega el valor como constante. Los reinicios son una comparación de generación en el hilo
dueño, solo en el camino donde el interruptor está encendido (STAMP_ON_CHANGE, NATIVE_STATE_REUSE) o una vez por
preparación de etapa (STAGE_MEMO). El hilo que lee el fichero nunca toca estado de otro hilo.

Las líneas de informe de cada modo (`[PERF][STAMP_ON_CHANGE]`, `[PERF][NATIVE_STATE]`, …) aparecen y desaparecen
con el interruptor.

## Rechazados

Cualquier otro nombre se rechaza (`not hot-safe`). Nada más está revisado; en general no lo son los que dimensionan
estructuras o arrancan hilos al inicio (número de workers, tamaños de anillos y pools), los que compilan shaders o
pipelines bajo el modo, y los que sólo se leen una vez al crear un objeto. De los diez de la lista no se rechazó
ninguno.

## Medir A/B

1. Arranca con `SHARPEMU_HOTFLAGS_FILE` y `SHARPEMU_FPS_LOG=1` (o `SHARPEMU_PROFILE_RENDER`), que escriben
   `[PERF][RENDER_MS] window_s=… frames=…` por ventana.
2. En la escena, alterna el fichero cada ~30 s (por ejemplo con un bucle que reescribe `IDLE_FLUSH=0` / `IDLE_FLUSH=4`).
3. Las líneas `[HOTFLAG]` y `[PERF][RENDER_MS]` salen del hilo de render en orden, así que el orden del log ya separa
   los segmentos. La ventana que contiene un cambio lo cruza: se descarta. Sumando `frames=` desde el arranque se
   reconstruye aproximadamente el eje de `frame=` (FpsLog cuenta cada presentación; el perfil de render, las que pasan por RecordFrame).

```
python scripts/hotflag_segments.py run.log --skip 5
```

Por segmento (entre grupos de cambios consecutivos): fotograma del cambio, segundos usados y descartados, FPS =
fotogramas / segundos de las ventanas conservadas y el estado de los interruptores que cambian en el log. Descarta
siempre la primera ventana tras un cambio y después hasta sumar `--skip` segundos. Al final agrupa por estado
(FPS agregado, media, mínimo y máximo de los segmentos). `--min-kept S` deja fuera segmentos con menos de S segundos.
