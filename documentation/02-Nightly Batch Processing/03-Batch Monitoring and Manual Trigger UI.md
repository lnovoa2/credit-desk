# Especificación Funcional: Monitoreo de Lote e Interfaz de Disparo Manual

**Módulo Padre**: Nightly Batch Processing | **Fecha**: 2026-08-10

## 1. Resumen Ejecutivo

- **Propósito**: Página web ASP.NET Web Forms que permite a los operadores consultar el estado del proceso de lote nocturno en tiempo real y forzar una ejecución inmediata del mismo fuera del ciclo automático.
- **Funcionalidad Clave**:
  1. Visualizar la marca temporal de la última ejecución del lote.
  2. Mostrar el contador acumulado de ejecuciones exitosas.
  3. Presentar el mensaje de resultado de la última ejecución (éxito o error).
  4. Listar las banderas de tiempo de ejecución por cliente almacenadas en memoria (`AUTO_BLOCKED`).
  5. Permitir el disparo sincrónico e inmediato del proceso de lote mediante un botón.
- **Usuarios**: Operadores del sistema con acceso a la consola web Legacy Credit Desk (sin control de autenticación individual en esta página).

---

## 2. Lógica de Negocio

### Procesos Clave

**Visualización de estado del lote**: Lectura de variables de estado global para presentar información actual al operador.
1. En cada carga de página (`Page_Load`) que no sea un postback, se invoca `Refresh()`.
2. `Refresh()` lee `LegacyState.LastBatchRun`, `LegacyState.BatchExecutions` y `LegacyState.LastBatchMessage`.
3. Se recorre `LegacyState.CustomerFlags` bajo bloqueo (`lock(SyncRoot)`) para construir la tabla HTML de banderas.
4. Si no existen banderas, se muestra el mensaje "No runtime flags yet".

**Disparo manual del lote**: Ejecución sincrónica del proceso de lote desde el hilo HTTP.
1. El operador hace clic en el botón "Run batch now".
2. El evento `Run_Click` llama a `NightlyBatch.RunNow()` de forma sincrónica en el hilo de la solicitud HTTP.
3. El proceso recalcula puntajes de riesgo y bloquea clientes con `OverdueDays >= 90`.
4. Una vez que `RunNow()` retorna, se invoca `Refresh()` para actualizar la pantalla con el nuevo estado.
5. La respuesta HTTP no se envía al navegador hasta que `RunNow()` complete su ejecución.

**Auto-refresco periódico**: Actualización automática de la interfaz sin intervención del operador.
1. Al cargar la página, el navegador ejecuta el fragmento JavaScript `setTimeout(window.location.reload, 10000)`.
2. Cada 10 segundos se emite un GET completo a la misma URL, recargando toda la página.
3. El ciclo se reinicia en cada carga, garantizando un refresco continuo mientras la pestaña esté abierta.

### Validaciones Críticas

| Campo / Condición | Validación | Comportamiento |
|---|---|---|
| Ejecución concurrente | `Interlocked.Exchange(ref _running, 1)` en `NightlyBatch.RunNow` | Retorna `"Batch already running"` sin ejecutar el lote |
| `LastBatchRun == DateTime.MinValue` | Comprobación en `Refresh()` | Muestra `"Never"` en lugar de una fecha inválida |
| `CustomerFlags` vacío | `hasFlags == false` al recorrer el `Hashtable` | Muestra `"No runtime flags yet"` en la tabla |

---

## 3. Componentes

### Backend

| Componente | Propósito | Responsabilidades Clave |
|---|---|---|
| `BatchPage` (`Batch.aspx.cs`) | Code-behind de la página de monitoreo | Maneja `Page_Load`, `Run_Click` y `Refresh()`; lee `LegacyState`; construye HTML de banderas |
| `NightlyBatch.RunNow()` | Ejecutor del proceso de lote | Recalcula `RiskScore`, aplica bloqueos automáticos, escribe auditoría, actualiza `LegacyState` |
| `LegacyState` | Estado global en memoria | Provee `LastBatchRun`, `BatchExecutions`, `LastBatchMessage`, `CustomerFlags` y `SyncRoot` |

### Frontend

| Componente | Propósito | Funcionalidad Principal |
|---|---|---|
| `Batch.aspx` | Página de monitoreo y disparo manual | Muestra estado del lote, tabla de banderas; botón de disparo; auto-refresco cada 10 s |
| `Site.master` | Plantilla maestra compartida | Proporciona encabezado, navegación global y contenedor de contenido |
| `Content/site.css` | Hoja de estilos | Aplica clases `btn` (botón), `grid` (tabla de banderas) y `box` (mensaje informativo) |

### Datos

| Entidad | Propósito | Campos Principales |
|---|---|---|
| `LegacyState.CustomerFlags` | Banderas volátiles por cliente (en memoria) | Clave: `CustomerId` (string); Valor: nombre de bandera (ej. `"AUTO_BLOCKED"`) |
| `LegacyState` (campos escalares) | Estado de ejecución del lote (en memoria) | `LastBatchRun` (DateTime), `BatchExecutions` (int), `LastBatchMessage` (string) |
| `Customers` (SQLite) | Datos de clientes | `Id`, `RiskScore`, `Blocked`, `OverdueDays`, `OutstandingDebt`, `CreditLimit` |
| `Audit` (SQLite) | Historial de ejecuciones del lote | `Type="BATCH"`, `Subject="CREDIT_RECALC"`, `Detail="Processed=N; Changed=M"` |

---

## 4. APIs y Endpoints

### Endpoints Principales

| Método | URL | Propósito | Auth |
|---|---|---|---|
| `GET` | `/Batch.aspx` | Carga inicial; llama `Refresh()` y muestra estado actual | No |
| `POST` | `/Batch.aspx` | Postback del botón "Run batch now"; invoca `RunNow()` y refresca estado | No |

**Ejemplo de flujo de datos en disparo manual**:
```
// Request (POST — postback ASP.NET Web Forms)
POST /Batch.aspx
__EVENTTARGET=Run&__VIEWSTATE=...

// Response (HTML renderizado tras RunNow)
LastRun  : "2026-08-10T14:35:22"
Executions: "12"
Message  : "Processed 47 customers; changed 3"
Flags table: [CustomerId=42 → AUTO_BLOCKED]
```

> No existen endpoints REST/JSON; toda la comunicación es postback HTTP estándar de ASP.NET Web Forms.

---

## 5. Flujos de Datos

### Flujo Principal: Disparo Manual del Lote

```
Operador → [Clic "Run batch now"]
         → POST /Batch.aspx (postback)
         → Run_Click()
         → NightlyBatch.RunNow()
              → LegacyRepository.Load()  →  SQLite (Customers)
              → recalcula RiskScore / Blocked por cada cliente
              → LegacyState.SetFlag(id, "AUTO_BLOCKED")  →  LegacyState.CustomerFlags
              → LegacyRepository.AddAudit(ds, "BATCH", "CREDIT_RECALC", detail)
              → LegacyRepository.Save(ds)  →  SQLite (Customers + Audit)
              → LegacyState.LastBatchRun / BatchExecutions / LastBatchMessage
         → Refresh()  →  lee LegacyState  →  actualiza controles UI
         → Respuesta HTML al navegador
```

**Pasos resumidos**:
1. El botón emite un postback; `Run_Click` llama `NightlyBatch.RunNow()` en el hilo HTTP.
2. `RunNow` carga todos los clientes desde SQLite, recalcula riesgo y aplica bloqueos.
3. Persiste cambios en SQLite e inserta registro de auditoría.
4. Actualiza `LegacyState` (variables volátiles en memoria).
5. `Refresh()` lee `LegacyState` y renderiza el estado actualizado en la página.

### Flujo Secundario: Auto-Refresco del Navegador

```
Navegador → setTimeout(10 s) → GET /Batch.aspx
          → Page_Load (no postback) → Refresh()
          → Lee LegacyState → Renderiza estado actual → HTML al navegador
```

### Manejo de Errores

| Error | Causa | Acción |
|---|---|---|
| `"Batch already running"` | `_running == 1` al entrar en `RunNow` (ejecución concurrente) | `RunNow` retorna el mensaje; `BatchMessage` label lo muestra; no se lanza excepción |
| `"ERROR: <mensaje>"` | Excepción no controlada dentro de `RunNow` | Se captura en `catch`, se almacena en `LegacyState.LastBatchMessage`; el temporizador continúa operando |
| Página bloqueada durante ejecución | `RunNow` tarda varios segundos en datasets grandes | El navegador permanece en espera hasta recibir respuesta; no hay timeout configurado explícitamente |

---

## 6. Integraciones

### Internas

- **Batch Engine - Timer and Concurrency Control** (`NightlyBatch`): La página invoca directamente `NightlyBatch.RunNow()` en el evento `Run_Click`. El mismo método es llamado periódicamente por el temporizador interno, por lo que el bloqueo de concurrencia (`_running`) es compartido entre ambas rutas de invocación.
- **Global In-Memory State** (`LegacyState`): La página lee únicamente `LegacyState` en `Refresh()`; nunca escribe directamente. Los datos mostrados (`LastBatchRun`, `BatchExecutions`, `LastBatchMessage`, `CustomerFlags`) son escritos por `NightlyBatch.RunNow()`.
- **Risk Score Recalculation and Auto-Block Logic** (dentro de `NightlyBatch.RunNow`): El disparo desde la UI activa el mismo flujo de recalculación que el temporizador automático, incluyendo bloqueos permanentes de clientes.
- **Batch Audit and State Update** (dentro de `NightlyBatch.RunNow`): El disparo manual provoca la inserción de un registro de auditoría en la tabla `Audit` de SQLite, idéntico al generado por el ciclo automático.
- **Web UI / Site.master**: La página se integra en la plantilla maestra compartida; hereda navegación global y estilos CSS.

### Externas

- Ninguna integración con sistemas externos. La página no consume ni expone servicios fuera del proceso ASP.NET.

---

## 7. Configuración

| Propiedad | Propósito | Default |
|---|---|---|
| `BatchIntervalSeconds` (`web.config`) | Intervalo en segundos entre ejecuciones automáticas del temporizador | `15` (mínimo efectivo: `10`) |
| `HardBlockOverdueDays` (`web.config`) | Umbral de días en mora para bloqueo automático de cliente | `90` |
| Auto-refresco (`Batch.aspx`, línea 5) | Intervalo de recarga completa de página en el navegador | `10000` ms (10 s), hardcodeado en markup |
| `HighRiskScore` (`web.config`) | Puntaje de riesgo considerado alto (usado por `CreditDecisionService`, no por esta página) | `72` |

---

## 8. Resumen

- **Componentes**: 2 backend (`BatchPage`, `NightlyBatch.RunNow` / `LegacyState`), 1 frontend (`Batch.aspx` + `Site.master`), 2 entidades de datos (`LegacyState` en memoria, tablas `Customers`/`Audit` en SQLite)
- **Endpoints**: 2 (GET carga inicial, POST disparo manual — ambos sobre `/Batch.aspx`)
- **Reglas de negocio**: 3 (protección contra ejecución concurrente, detección de primer arranque, tabla de banderas vacía)
- **Integraciones**: 4 internas (Batch Engine, Global State, Risk Recalculation, Web UI master), 0 externas

---
**Nota**: Documento generado a partir del análisis directo del código fuente. La ejecución sincrónica de `RunNow()` en el hilo HTTP y el auto-refresco por recarga completa son limitaciones de diseño de la implementación legacy relevantes para una eventual migración.
