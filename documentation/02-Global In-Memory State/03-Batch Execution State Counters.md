# Especificación Funcional: Contadores de Estado de Ejecución de Batch

**Módulo Padre**: Global In-Memory State | **Fecha**: 2026-08-10

## 1. Resumen Ejecutivo

- **Propósito**: Mantener en memoria, con alcance de proceso, los tres campos de telemetría de ejecución del batch nocturno: marca de tiempo del último ciclo, mensaje de resultado legible y contador acumulado de ejecuciones desde el inicio de la aplicación.
- **Funcionalidad Clave**:
  - Almacenar `LastBatchRun` (cuándo fue la última ejecución).
  - Almacenar `LastBatchMessage` (resumen textual del resultado o error).
  - Incrementar `BatchExecutions` tras cada ciclo completado.
  - Exponer estos valores a la interfaz de monitorización `Batch.aspx` sin acceso a la base de datos.
  - Reflejar el mensaje de error en caso de fallo, manteniendo el estado observable en tiempo real.
- **Usuarios**: Operadores y administradores que acceden a la página `Batch.aspx` para monitorizar o disparar manualmente el proceso batch.

---

## 2. Lógica de Negocio

### Procesos Clave

**Actualización de contadores tras ejecución exitosa**:
1. `NightlyBatch.RunNow()` completa el ciclo de recálculo de riesgo y bloqueo de clientes.
2. Se asigna `LegacyState.LastBatchRun = DateTime.Now`.
3. Se incrementa `LegacyState.BatchExecutions++`.
4. Se asigna `LegacyState.LastBatchMessage` con el resumen ("Processed X customers; changed Y").
5. `Batch.aspx.cs` lee los tres campos en el siguiente ciclo de recarga y los muestra al operador.

**Actualización de contadores tras error**:
1. `NightlyBatch.RunNow()` captura la excepción en el bloque `catch`.
2. Se asigna `LegacyState.LastBatchMessage = "ERROR: " + ex.Message`.
3. `LastBatchRun` y `BatchExecutions` **no se actualizan** en caso de error (sólo el mensaje refleja el fallo).

**Ejecución manual desde la UI**:
1. El operador pulsa "Run batch now" en `Batch.aspx`.
2. `Run_Click` invoca `NightlyBatch.RunNow()`.
3. Tras la ejecución, `Refresh()` vuelve a leer los tres campos de `LegacyState` y actualiza los controles `Label` de la página.

### Validaciones Críticas

| Campo | Validación | Comportamiento |
|-------|------------|----------------|
| `BatchExecutions` | Sin límite superior definido | Se incrementa indefinidamente mientras el proceso esté activo |
| `LastBatchRun` | Inicializado a `DateTime.MinValue` | La UI muestra "Never" cuando el valor es `MinValue` |
| `LastBatchMessage` | Inicializado a `"Never executed"` | Refleja el último resultado o el mensaje de error más reciente |
| Concurrencia del batch | `Interlocked.Exchange` en `_running` (en `NightlyBatch`) | Impide ejecuciones solapadas; devuelve `"Batch already running"` |

### Riesgo Técnico Documentado

Los tres campos son mutados por `NightlyBatch.RunNow()` **sin adquirir `lock(LegacyState.SyncRoot)`**. El objeto `SyncRoot` existe en `LegacyState` y es utilizado por los accesores de `CustomerFlags`, pero las asignaciones directas a `LastBatchRun`, `LastBatchMessage` y `BatchExecutions` quedan fuera del bloque de cerrojo. Dado que el timer es único (`System.Threading.Timer` con un solo callback activo a la vez, reforzado por `Interlocked`), la condición de carrera es improbable en la práctica, pero constituye una deuda técnica de migración.

---

## 3. Componentes

### Backend

| Componente | Propósito | Responsabilidades Clave |
|-----------|-----------|------------------------|
| `LegacyState.cs` (campos líneas 8–10) | Contenedor estático de los tres contadores | Declarar y exponer `LastBatchRun`, `LastBatchMessage`, `BatchExecutions` como campos públicos estáticos |
| `NightlyBatch.cs` — `RunNow()` | Escritor exclusivo de los contadores | Actualizar los tres campos al finalizar cada ciclo (éxito o error) |

### Frontend

| Componente | Propósito | Funcionalidad Principal |
|-----------|-----------|------------------------|
| `Batch.aspx` / `Batch.aspx.cs` | Pantalla de monitorización del batch | Mostrar `LastBatchRun`, `BatchExecutions` y `LastBatchMessage`; botón de ejecución manual; auto-recarga cada 10 s |

### Datos

| Campo | Tipo | Propósito | Valor Inicial |
|-------|------|-----------|---------------|
| `LastBatchRun` | `DateTime` | Marca de tiempo de la última ejecución completada con éxito | `DateTime.MinValue` |
| `LastBatchMessage` | `string` | Mensaje legible del resultado (éxito o error) del último ciclo | `"Never executed"` |
| `BatchExecutions` | `int` | Contador acumulado de ciclos completados con éxito desde el inicio del proceso | `0` |

> **Nota**: Estos campos son volátiles — no se persisten en SQLite. Un reinicio de la aplicación los restablece a sus valores iniciales.

---

## 4. APIs y Endpoints

Este submódulo no expone endpoints HTTP propios. La lectura de los contadores se realiza directamente en el code-behind de `Batch.aspx`.

### Endpoints Relacionados (vía `Batch.aspx`)

| Método | URL | Propósito | Autenticación |
|--------|-----|-----------|---------------|
| GET | `/Batch.aspx` | Renderizar el panel de monitorización del batch | No (aplicación interna) |
| POST | `/Batch.aspx` | Disparar ejecución manual del batch (`Run_Click`) | No (aplicación interna) |

**Flujo de datos en GET `/Batch.aspx`** (lectura de contadores):
```
// Lógica en Batch.aspx.cs — método Refresh()
LastRun.Text      ← LegacyState.LastBatchRun   (formateado "s" o "Never")
Executions.Text   ← LegacyState.BatchExecutions (ToString)
BatchMessage.Text ← LegacyState.LastBatchMessage
```

**Flujo de datos en POST `/Batch.aspx`** (ejecución manual):
```
// Run_Click → NightlyBatch.RunNow() → actualiza LegacyState → Refresh()
Request: POST /Batch.aspx (EventTarget=Run)
Efecto:  LegacyState.LastBatchRun     = DateTime.Now
         LegacyState.BatchExecutions  += 1
         LegacyState.LastBatchMessage = "Processed N customers; changed M"
Response: Página recargada con valores actualizados
```

---

## 5. Flujos de Datos

### Flujo Principal: Ciclo Automático del Batch

```
Timer (cada N segundos)
    → NightlyBatch.Tick()
    → NightlyBatch.RunNow()
        → Interlocked.Exchange(_running, 1)  [guard concurrencia]
        → LegacyRepository.Load()            [cargar clientes desde SQLite]
        → Recálculo de riesgo y bloqueos     [bucle sobre DataTable Customers]
        → LegacyRepository.Save(ds)          [persistir cambios]
        → LegacyState.LastBatchRun     = DateTime.Now
        → LegacyState.BatchExecutions  += 1
        → LegacyState.LastBatchMessage = "Processed N; changed M"
        → Interlocked.Exchange(_running, 0)  [liberar guard]
```

**Pasos resumidos**:
1. El timer de `NightlyBatch` dispara `Tick()` según el intervalo configurado.
2. La guarda `Interlocked` impide ejecuciones simultáneas.
3. Tras completar el procesamiento y la persistencia, los tres campos de `LegacyState` se actualizan.
4. `Batch.aspx` lee los campos en la próxima carga de página (o auto-recarga cada 10 s).

### Flujo Alternativo: Error en el Batch

```
NightlyBatch.RunNow()
    → catch(Exception ex)
        → LegacyState.LastBatchMessage = "ERROR: " + ex.Message
        [LastBatchRun y BatchExecutions NO se modifican]
    → finally: Interlocked.Exchange(_running, 0)
```

### Manejo de Errores

| Situación | Causa | Comportamiento |
|-----------|-------|----------------|
| Excepción durante el procesamiento | Error en BD, datos corruptos, etc. | `LastBatchMessage` recibe `"ERROR: ..."`. `LastBatchRun` y `BatchExecutions` no se actualizan |
| Batch ya en ejecución | Llamada concurrente a `RunNow()` | Retorna `"Batch already running"` sin modificar ningún campo de estado |
| Proceso reiniciado | Reinicio de aplicación ASP.NET | Todos los contadores se restablecen a valores iniciales (`MinValue`, `"Never executed"`, `0`) |

---

## 6. Integraciones

### Internas

- **NightlyBatch** (`NightlyBatch.cs`): Escritor exclusivo de los tres campos — los actualiza tras cada ciclo de recálculo.
- **Batch.aspx / BatchPage** (`Batch.aspx.cs`): Lector de los tres campos — los muestra en el panel de monitorización y en la tabla de flags de `CustomerFlags`.
- **Shared Lock and Synchronisation Root** (`LegacyState.SyncRoot`): Objeto de cerrojo compartido con el resto de `LegacyState`. Los contadores de batch **no utilizan** este cerrojo actualmente, a diferencia de los métodos `SetFlag`/`GetFlag`.

### Externas

- Ninguna. Los contadores son puramente en memoria y no interactúan con sistemas externos.

---

## 7. Configuración

| Propiedad | Ubicación | Propósito | Valor por Defecto |
|-----------|-----------|-----------|-------------------|
| `BatchIntervalSeconds` | `web.config` → `<appSettings>` | Intervalo en segundos entre ejecuciones automáticas del timer | `45` s (mínimo forzado: 10 s) |

> La configuración del intervalo afecta indirectamente la frecuencia de actualización de los contadores, pero no la lógica de los campos en sí.

---

## 8. Resumen

- **Componentes**: 1 backend (campos en `LegacyState.cs`), 1 frontend (`Batch.aspx`/`Batch.aspx.cs`), 0 tablas de datos propias (estado volátil en memoria)
- **Endpoints**: 2 (GET y POST sobre `/Batch.aspx`)
- **Reglas de negocio**: 3 (actualización en éxito, actualización parcial en error, guarda de concurrencia)
- **Integraciones**: 3 internas (`NightlyBatch`, `Batch.aspx`, `SyncRoot`), 0 externas

---
**Nota**: Mantener documento < 1500 líneas. Se ha priorizado la información funcional esencial sobre los detalles de implementación de bajo nivel.
