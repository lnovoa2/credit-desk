# Especificación Funcional: Página de Monitor de Lote y Disparador Manual

**Módulo Padre**: Web User Interface (ASP.NET Web Forms) | **Fecha**: 2026-08-10

## 1. Resumen Ejecutivo
- **Propósito**: Página de operaciones que muestra el estado de ejecución del proceso batch nocturno de crédito y permite forzar una ejecución inmediata de forma manual, fuera del ciclo automático temporizador.
- **Funcionalidad Clave**:
  1. Visualización en tiempo cuasi-real del estado del último batch (timestamp, contador de ejecuciones, mensaje de resultado)
  2. Disparador manual de ejecución batch bajo demanda mediante botón de acción
  3. Listado de indicadores de bloqueo automático por cliente (`AUTO_BLOCKED`) almacenados en memoria
  4. Auto-recarga de la página cada 10 segundos vía JavaScript del lado cliente
- **Usuarios**: Operadores de crédito / administradores del sistema con acceso a la aplicación web interna

---

## 2. Lógica de Negocio

### Procesos Clave

**Visualización de Estado Batch**: Lectura del estado de ejecución del proceso batch desde el almacén en memoria global
1. En cada carga de página (no-postback), se invocan los valores de `LegacyState`
2. Se muestra la fecha/hora de última ejecución (formato ISO 8601 `s`), el número total de ejecuciones y el último mensaje de resultado
3. Si el batch nunca se ha ejecutado, `LastBatchRun` contiene `DateTime.MinValue` y se muestra el literal `"Never"`

**Ejecución Manual del Batch**: Forzar inmediatamente el procesamiento de riesgo crediticio sobre todos los clientes
1. El usuario pulsa el botón "Run batch now"
2. El evento `Run_Click` llama a `NightlyBatch.RunNow()` de forma síncrona en el hilo de la petición HTTP
3. `NightlyBatch.RunNow()` usa `Interlocked.Exchange` para evitar ejecuciones concurrentes; si ya hay una en curso, retorna sin procesar
4. Tras la ejecución, se refresca el panel de estado con los nuevos valores de `LegacyState`

**Lectura de Indicadores de Cliente en Memoria**: Visualizar el diccionario de flags de bloqueo automático
1. Se adquiere `lock(LegacyState.SyncRoot)` para evitar lecturas parciales mientras el batch temporizador puede estar escribiendo concurrentemente
2. Se itera `LegacyState.CustomerFlags` (Hashtable) y se construye una tabla HTML con las claves (ID de cliente) y los valores de flag
3. Si no hay ningún flag registrado, se muestra la fila `"No runtime flags yet"`

### Validaciones Críticas
| Campo / Condición | Regla | Comportamiento |
|---|---|---|
| Batch ya en ejecución | `_running == 1` (flag atómico) | `RunNow()` retorna `"Batch already running"` sin procesar |
| `LastBatchRun == DateTime.MinValue` | Batch nunca ejecutado | Etiqueta muestra `"Never"` en lugar de fecha |
| Lectura de `CustomerFlags` | Requiere `lock(LegacyState.SyncRoot)` | Garantiza coherencia con escrituras del timer concurrente |

---

## 3. Componentes

### Backend
| Componente | Propósito | Responsabilidades Clave |
|---|---|---|
| `Batch.aspx.cs` (`BatchPage`) | Controlador de la página | Leer estado de `LegacyState`, invocar `NightlyBatch.RunNow()`, construir tabla HTML de flags |
| `NightlyBatch.RunNow()` | Motor de ejecución batch | Cargar clientes desde BD, recalcular riesgo, bloquear clientes con ≥90 días vencidos, persistir cambios, actualizar `LegacyState` |
| `LegacyState` | Almacén de estado en memoria | Mantener timestamp de última ejecución, contador, mensaje de resultado y diccionario de flags de cliente |

### Frontend
| Componente | Propósito | Funcionalidad Principal |
|---|---|---|
| `Batch.aspx` | Página de monitorización | Mostrar estado batch, botón de disparo manual, tabla de flags de cliente, auto-recarga cada 10 s |
| `LastRun` (Label) | Indicador temporal | Muestra timestamp de última ejecución en formato ISO 8601 |
| `Executions` (Label) | Contador acumulado | Número total de ejecuciones del batch desde el inicio de la aplicación |
| `BatchMessage` (Label) | Mensaje de resultado | Resumen del último batch: clientes procesados y modificados, o mensaje de error |
| `Run` (Button) | Disparador manual | Fuerza ejecución inmediata del batch; dispara postback con `Run_Click` |
| `Flags` (Literal) | Tabla de indicadores | HTML generado en servidor con los flags por cliente del estado en memoria |
| Script inline JS | Auto-refresco | `setTimeout(window.location.reload, 10000)` — recarga completa cada 10 segundos |

### Datos
| Entidad | Propósito | Campos Principales |
|---|---|---|
| `LegacyState.LastBatchRun` | Timestamp de última ejecución | `DateTime` (en memoria, no persistido) |
| `LegacyState.BatchExecutions` | Contador de ejecuciones | `int` (en memoria, no persistido) |
| `LegacyState.LastBatchMessage` | Último mensaje de resultado | `string` (en memoria, no persistido) |
| `LegacyState.CustomerFlags` | Flags de clientes bloqueados automáticamente | `Hashtable<customerId, flagValue>` (en memoria, no persistido) |
| Tabla `Customers` (BD SQLite) | Datos de clientes procesados por el batch | `Id`, `OverdueDays`, `OutstandingDebt`, `CreditLimit`, `RiskScore`, `Blocked` |
| Tabla `Audit` (BD SQLite) | Registro de ejecuciones del batch | Acción `CREDIT_RECALC`, actor `BATCH`, descripción con conteos |

---

## 4. APIs y Endpoints

### Endpoints Principales
| Método | URL | Propósito | Auth |
|---|---|---|---|
| GET | `/Batch.aspx` | Cargar página de monitorización con estado actual del batch | No (sin autenticación en la app) |
| POST | `/Batch.aspx` (postback) | Disparar ejecución inmediata del batch (botón "Run batch now") | No |

**Ejemplo de Flujo Postback (Disparo Manual)**:
```
// Request (postback ASP.NET Web Forms)
POST /Batch.aspx
__VIEWSTATE=...&__EVENTTARGET=Run&...

// Procesamiento servidor
NightlyBatch.RunNow() → LegacyRepository.Load() → recalcular riesgo → LegacyRepository.Save()
LegacyState actualizado → Refresh() renderiza nuevos valores en Labels y Flags Literal

// Response
HTTP 200 — página HTML completa con estado actualizado
```

---

## 5. Flujos de Datos

### Flujo Principal: Carga de Página (Lectura de Estado)

```
Usuario (GET /Batch.aspx)
    → Page_Load [!IsPostBack]
        → Refresh()
            → LegacyState.LastBatchRun → Label "LastRun"
            → LegacyState.BatchExecutions → Label "Executions"
            → LegacyState.LastBatchMessage → Label "BatchMessage"
            → lock(LegacyState.SyncRoot)
                → iterar CustomerFlags → HTML tabla → Literal "Flags"
    → Respuesta HTML con estado + script auto-recarga 10 s
```

**Pasos resumidos**:
1. Petición GET llega; `Page_Load` detecta que no es postback y llama a `Refresh()`
2. `Refresh()` lee tres propiedades de `LegacyState` sin locking (lectura atómica de valores primitivos)
3. Adquiere `SyncRoot` para leer el `Hashtable` de flags de forma segura en concurrencia
4. Genera HTML de la tabla de flags y lo asigna al `Literal`
5. El cliente recibe la página completa y el script JS programa recarga automática en 10 segundos

### Flujo Secundario: Disparo Manual del Batch

```
Usuario → "Run batch now" → POST /Batch.aspx
    → Run_Click
        → NightlyBatch.RunNow()
            → Interlocked.Exchange: verificar no ejecución concurrente
            → LegacyRepository.Load() → DataSet completo
            → Por cada cliente: recalcular RiskScore
                → OverdueDays > 60 → +9 puntos
                → OverdueDays > 30 → +4 puntos
                → OverdueDays == 0 && Deuda < 25% límite → -2 puntos
                → Deuda > Límite → +7 puntos (clamp 0-100)
            → Si OverdueDays ≥ 90 y no bloqueado → Blocked=true + LegacyState.SetFlag("AUTO_BLOCKED")
            → LegacyRepository.AddAudit(BATCH, CREDIT_RECALC, ...)
            → LegacyRepository.Save(ds)
            → Actualizar LegacyState (timestamp, contador, mensaje)
        → Refresh() → actualiza UI con nuevo estado
```

**Pasos resumidos**:
1. Postback detectado; `Run_Click` invoca `NightlyBatch.RunNow()` síncronamente
2. Protección contra concurrencia mediante flag atómico `_running`
3. Proceso recalcula riesgo para todos los clientes; aplica bloqueo automático a morosos graves (≥90 días)
4. Persistencia en SQLite y actualización del estado global en memoria
5. Página se refresca mostrando los resultados actualizados

### Manejo de Errores
| Error | Causa | Acción |
|---|---|---|
| `"Batch already running"` | `RunNow()` llamado mientras ya hay ejecución en curso | Retorna mensaje sin procesar; `LegacyState.LastBatchMessage` no se actualiza |
| `"ERROR: <mensaje>"` | Excepción no controlada durante el procesamiento | `catch` captura la excepción, escribe en `LegacyState.LastBatchMessage`, flag atómico se libera en `finally` |
| Sin flags en tabla | `CustomerFlags` vacío (ningún cliente bloqueado automáticamente aún) | Muestra fila `"No runtime flags yet"` |

---

## 6. Integraciones

### Internas
- **Nightly Batch Processing (`NightlyBatch`)**: Ejecución bajo demanda — la página llama a `RunNow()`, el mismo método que usa el timer automático
- **Global In-Memory State (`LegacyState`)**: Fuente de datos de estado — lectura de `LastBatchRun`, `BatchExecutions`, `LastBatchMessage`, `CustomerFlags`
- **Data Access and Persistence Layer (`LegacyRepository`)**: Accedido indirectamente a través de `NightlyBatch.RunNow()` — carga `DataSet`, graba cambios en SQLite, escribe registro de auditoría
- **Master Page Layout and Navigation (`Site.master`)**: Herencia de estructura HTML, navegación y hoja de estilos común

### Externas
- Ninguna

---

## 7. Configuración

| Propiedad | Propósito | Default |
|---|---|---|
| `BatchIntervalSeconds` (`web.config`) | Intervalo del timer automático del batch (segundos) | `45` (mínimo forzado: `10`) |
| `HardBlockOverdueDays` (documentado en `web.config`) | Umbral de días de mora para bloqueo automático | `90` (hardcoded en `NightlyBatch.cs:55`) |
| `HighRiskScore` (`web.config`) | Umbral de puntuación de riesgo alto (usado en `CreditDecisionService`, no directamente en batch UI) | Según `web.config` |
| Auto-recarga JS | Intervalo de recarga automática de la página | `10000` ms (hardcoded en `Batch.aspx:5`) |

---

## 8. Resumen

- **Componentes**: 3 backend (`BatchPage`, `NightlyBatch`, `LegacyState`), 7 frontend (Labels ×3, Button, Literal, script JS, página maestra heredada), 2 entidades de datos (en memoria: `LegacyState`; persistidas: `Customers`, `Audit`)
- **Endpoints**: 2 (GET carga inicial, POST disparo manual via postback)
- **Reglas de negocio**: 3 (protección contra ejecución concurrente, recálculo de riesgo con 4 reglas de ajuste, bloqueo automático irreversible por mora ≥90 días)
- **Integraciones**: 4 internas (`NightlyBatch`, `LegacyState`, `LegacyRepository`, `Site.master`), 0 externas

---
**Nota**: Mantener documento < 1500 líneas. Priorizar información funcional esencial.
