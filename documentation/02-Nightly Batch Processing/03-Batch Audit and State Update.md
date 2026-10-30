# Especificación Funcional: Auditoría de Lote y Actualización de Estado

**Módulo Padre**: Nightly Batch Processing | **Fecha**: 2026-08-10

## 1. Resumen Ejecutivo

- **Propósito**: Registrar el resultado de cada ejecución del proceso batch en la tabla de auditoría persistente y actualizar los contadores de estado volátiles en memoria de proceso, garantizando la continuidad del temporizador ante cualquier fallo.
- **Funcionalidad Clave**:
  - Insertar una fila en la tabla `Audit` con tipo `BATCH` / asunto `CREDIT_RECALC` y detalle con los totales procesados y modificados.
  - Persistir todos los cambios del `DataSet` (clientes recalculados + fila de auditoría) en SQLite mediante una única transacción.
  - Actualizar tres contadores en `LegacyState`: `LastBatchRun`, `BatchExecutions` y `LastBatchMessage`.
  - Capturar cualquier excepción sin relanzarla, almacenando el mensaje de error en `LastBatchMessage` para que el temporizador no se detenga.
  - Liberar el candado de concurrencia (`_running = 0`) en el bloque `finally`, incluso si ocurre un error.
- **Usuarios**: No tiene interfaz propia. Sus datos son consumidos por operadores a través de `Batch.aspx` (panel de monitorización del proceso batch).

---

## 2. Lógica de Negocio

### Procesos Clave

**Escritura de auditoría y persistencia (ruta exitosa)**:
1. El submódulo previo (*Risk Score Recalculation and Auto-Block Logic*) completa el bucle de recálculo y devuelve el contador `changed`.
2. Se llama a `LegacyRepository.AddAudit(ds, "BATCH", "CREDIT_RECALC", "Processed=N; Changed=M")`, que añade una nueva fila al `DataTable` `Audit` en memoria con un GUID nuevo, la marca de tiempo actual y los totales.
3. Se llama a `LegacyRepository.Save(ds)`, que abre una transacción SQLite y persiste las filas modificadas de `Customers` y la nueva fila de `Audit` de forma atómica.
4. Se actualizan los tres campos de `LegacyState`: `LastBatchRun` ← `DateTime.Now`, `BatchExecutions` se incrementa en 1, `LastBatchMessage` ← texto de resumen.
5. `RunNow` retorna el mensaje de resumen.

**Captura de errores (ruta de excepción)**:
1. Cualquier excepción producida dentro del bloque `try` (incluido el recálculo previo) es capturada por el bloque `catch`.
2. `LegacyState.LastBatchMessage` se actualiza con `"ERROR: " + ex.Message`.
3. El error **no se relanza**: el temporizador de fondo no termina y el próximo tick se ejecutará normalmente.
4. `BatchExecutions` **no** se incrementa en la ruta de error.

**Liberación del candado de concurrencia**:
1. El bloque `finally` (ejecutado siempre, tanto en éxito como en error) llama a `Interlocked.Exchange(ref _running, 0)`.
2. Esto libera el candado que previene ejecuciones concurrentes, permitiendo el siguiente tick o activación manual.

### Validaciones Críticas

| Campo / Condición | Validación | Comportamiento |
|---|---|---|
| `_running` al entrar en `RunNow` | `Interlocked.Exchange` comprueba si ya vale `1` | Retorna `"Batch already running"` sin ejecutar nada |
| Excepción en cualquier punto del `try` | Bloque `catch (Exception ex)` | Almacena mensaje en `LastBatchMessage`; no relanza |
| Liberación del candado | Bloque `finally` incondicional | `_running` siempre vuelve a `0` tras `RunNow` |

---

## 3. Componentes

### Backend

| Componente | Propósito | Responsabilidades Clave |
|---|---|---|
| `NightlyBatch.RunNow` (líneas 63–78) | Fase de cierre de cada ejecución batch | Llamar a `AddAudit`, llamar a `Save`, actualizar `LegacyState`, capturar excepciones, liberar `_running` en `finally` |
| `LegacyRepository.AddAudit` | Crear fila de auditoría en el `DataSet` | Generar GUID, asignar `At = DateTime.Now`, `Type = "BATCH"`, `Subject = "CREDIT_RECALC"`, `Detail = "Processed=N; Changed=M"` |
| `LegacyRepository.Save` | Persistir el `DataSet` en SQLite | Abrir transacción, guardar `Customers` modificados + nueva fila `Audit`, hacer `Commit` |
| `LegacyState` | Almacén de estado volátil en memoria | Mantener `LastBatchRun`, `BatchExecutions`, `LastBatchMessage` accesibles para la UI |

### Frontend

| Componente | Propósito | Funcionalidad Principal |
|---|---|---|
| `Batch.aspx` / `BatchPage` | Visualización del estado batch | Muestra `LastBatchRun`, `BatchExecutions`, `LastBatchMessage` leídos de `LegacyState`; auto-refresco cada 10 s |

### Datos

| Entidad | Propósito | Campos Principales |
|---|---|---|
| `Audit` (tabla SQLite) | Registro persistente de ejecuciones batch | `Id` (GUID), `At` (timestamp), `Type` (`BATCH`), `Subject` (`CREDIT_RECALC`), `Detail` (`Processed=N; Changed=M`) |
| `LegacyState` (en memoria) | Estado volátil de proceso | `LastBatchRun` (DateTime), `BatchExecutions` (int), `LastBatchMessage` (string), `CustomerFlags` (Hashtable) |

---

## 4. APIs y Endpoints

Este submódulo no expone endpoints HTTP propios. La única superficie de invocación es:

### Puntos de Entrada

| Caller | Método invocado | Contexto |
|---|---|---|
| `NightlyBatch.Tick` (temporizador) | `NightlyBatch.RunNow()` | Cada N segundos según `BatchIntervalSeconds` |
| `BatchPage.Run_Click` (HTTP POST) | `NightlyBatch.RunNow()` | Click en botón "Run batch now" en `Batch.aspx` |

**Firma de retorno de `RunNow`**:
```
// Éxito
"Processed 5 customers; changed 3"

// Concurrencia
"Batch already running"

// Error
"ERROR: <mensaje de excepción>"
```

**Fila de auditoría generada (ejemplo)**:
```
Id:      "a3f2b1c4d5e6f7a8b9c0d1e2f3a4b5c6"
At:      2026-08-10T02:15:00.000Z
Type:    "BATCH"
Subject: "CREDIT_RECALC"
Detail:  "Processed=5; Changed=2"
```

---

## 5. Flujos de Datos

### Flujo Principal: Cierre de ejecución batch

```
[Bucle de recálculo completado]
        |
        v
AddAudit(ds, "BATCH", "CREDIT_RECALC", "Processed=N; Changed=M")
        |  (fila nueva en DataTable Audit en memoria)
        v
LegacyRepository.Save(ds)
        |  (transacción SQLite: UPDATE Customers + INSERT Audit)
        v
LegacyState.LastBatchRun = DateTime.Now
LegacyState.BatchExecutions++
LegacyState.LastBatchMessage = "Processed N customers; changed M"
        |
        v
return LastBatchMessage
        |
        v  [finally]
Interlocked.Exchange(ref _running, 0)
```

**Pasos resumidos**:
1. Auditoría añadida al `DataSet` en memoria.
2. Todo el `DataSet` persistido en SQLite en una transacción atómica.
3. Estado volátil actualizado en `LegacyState` (solo en ruta exitosa para `BatchExecutions`).
4. Mensaje de resultado devuelto al caller.
5. Candado de concurrencia liberado incondicionalmente.

### Flujo de Error

```
[Excepción en cualquier punto del try]
        |
        v catch(Exception ex)
LegacyState.LastBatchMessage = "ERROR: " + ex.Message
        |  (BatchExecutions NO se incrementa)
        v return LastBatchMessage
        |
        v  [finally]
Interlocked.Exchange(ref _running, 0)
```

### Manejo de Errores

| Error | Causa | Acción |
|---|---|---|
| Excepción de base de datos (SQLite) | Fallo en `Save` — disco lleno, BD bloqueada | Capturada; mensaje en `LastBatchMessage`; candado liberado; timer continúa |
| Excepción en recálculo previo | Error aritmético, dato nulo | Misma captura; el DataSet puede quedar en estado parcial (no guardado) |
| Ejecución concurrente | `_running == 1` al entrar | Retorno inmediato `"Batch already running"`; sin efecto en estado |

---

## 6. Integraciones

### Internas

- **Data Access and Persistence Layer (`LegacyRepository`)**: `AddAudit` crea la fila de auditoría en el `DataSet`; `Save` la persiste junto con los cambios de `Customers` en una sola transacción SQLite.
- **Global In-Memory State (`LegacyState`)**: Se escriben `LastBatchRun`, `BatchExecutions` y `LastBatchMessage` en la ruta exitosa; solo `LastBatchMessage` en la ruta de error. Los tres campos son leídos por `Batch.aspx`.
- **Risk Score Recalculation and Auto-Block Logic**: Submódulo previo que produce el `DataSet` modificado y el contador `changed` consumidos aquí.
- **Batch Engine - Timer and Concurrency Control**: Proporciona el campo `_running` liberado en el `finally`; invoca `RunNow` mediante `Tick`.
- **Batch Monitoring and Manual Trigger UI (`Batch.aspx`)**: Lee `LegacyState` en cada carga de página para mostrar el estado al operador.

### Externas

Ninguna. Este submódulo no llama a servicios externos ni APIs de terceros.

---

## 7. Configuración

| Propiedad | Propósito | Valor / Default |
|---|---|---|
| `BatchIntervalSeconds` (`web.config`) | Intervalo entre ticks del temporizador | Default `45`; mínimo `10`; configurable |
| `LegacyState.BatchExecutions` | Contador acumulado de ejecuciones exitosas | Inicia en `0`; se incrementa en cada ejecución sin error; **no persiste** entre reinicios |
| `LegacyState.LastBatchRun` | Timestamp de la última ejecución exitosa | `DateTime.MinValue` hasta la primera ejecución; **no persiste** entre reinicios |
| `LegacyState.LastBatchMessage` | Mensaje del último resultado (éxito o error) | `"Never executed"` hasta la primera ejecución; **no persiste** entre reinicios |

---

## 8. Resumen

- **Componentes**: 2 backend (`NightlyBatch.RunNow` fase de cierre, `LegacyRepository`), 1 frontend (`Batch.aspx`), 2 datos (`Audit` en SQLite, `LegacyState` en memoria)
- **Endpoints**: 0 propios (invocado internamente por timer y por `Batch.aspx`)
- **Reglas de negocio**: 3 (`BatchExecutions` solo en éxito; errores silenciados para preservar el timer; `_running` liberado incondicionalmente en `finally`)
- **Integraciones**: 5 internas, 0 externas

---
**Nota**: Mantener documento < 1500 líneas. Priorizar información funcional esencial.
