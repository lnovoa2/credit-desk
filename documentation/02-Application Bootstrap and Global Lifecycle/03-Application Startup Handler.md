# Especificación Funcional: Manejador de Arranque de la Aplicación

**Módulo Padre**: Application Bootstrap and Global Lifecycle | **Fecha**: 2026-08-10

## 1. Resumen Ejecutivo

- **Propósito**: Bootstrapea toda la aplicación ASP.NET en el primer acceso. Es el punto de entrada único del ciclo de vida: garantiza que la base de datos SQLite exista con su esquema y datos semilla, y arma el temporizador de procesamiento nocturno por lotes antes de que se sirva cualquier petición HTTP.
- **Funcionalidad Clave**:
  1. Creación idempotente del esquema SQLite y directorio `App_Data` si no existen.
  2. Inserción de datos semilla (5 clientes y 2 pedidos de muestra) en primer arranque.
  3. Inicio del temporizador de `NightlyBatch` con el intervalo configurado en `web.config`.
  4. Registro de un evento de auditoría `BOOTSTRAP` en la base de datos al crear la BD por primera vez.
  5. Ejecución garantizada mediante el lock compartido `LegacyState.SyncRoot` para evitar condiciones de carrera.
- **Usuarios**: No hay interacción de usuario final. Lo ejecuta el runtime de ASP.NET automáticamente.

---

## 2. Lógica de Negocio

### Procesos Clave

**Arranque de la aplicación** (`Application_Start` → `Global.asax:3-7`):
1. El runtime ASP.NET dispara `Application_Start` antes de procesar la primera petición HTTP.
2. Se invoca `LegacyRepository.EnsureDatabase()`: crea el directorio `App_Data` si no existe, ejecuta el DDL del esquema con `CREATE TABLE/INDEX/VIEW/TRIGGER IF NOT EXISTS`, y si la tabla `Customers` está vacía llama a `SeedDatabase()`.
3. Se invoca `NightlyBatch.Start()`: lee `BatchIntervalSeconds` de `web.config` (default 45 s, mínimo 10 s) y crea un `System.Threading.Timer` periódico.

**Inicialización de la base de datos** (`LegacyRepository.EnsureDatabase` → `LegacyRepository.cs:24-42`):
1. Adquiere el lock `LegacyState.SyncRoot`.
2. Determina si el archivo `.db` existe ya (`firstRun`).
3. Ejecuta el bloque DDL completo (idempotente por `IF NOT EXISTS`).
4. Si `firstRun == true` o `COUNT(*) FROM Customers == 0`, ejecuta `SeedDatabase()`.

**Siembra de datos** (`SeedDatabase` → `LegacyRepository.cs:281-303`):
1. Inserta 5 clientes de muestra con `INSERT OR IGNORE` en una transacción única.
2. Inserta 2 pedidos semilla (`REVIEW` y `APPROVED`).
3. Inserta registro de auditoría tipo `SYSTEM` / sujeto `BOOTSTRAP`.
4. Confirma la transacción (`tx.Commit()`).

### Validaciones Críticas

| Campo | Validación | Comportamiento |
|-------|------------|----------------|
| `BatchIntervalSeconds` | Valor mínimo 10 s | Si el valor parseado es < 10, se fuerza a 10 s |
| Directorio `App_Data` | Debe existir antes de abrir SQLite | Se crea automáticamente si no existe |
| Tabla `Customers` | `COUNT(*) == 0` | Activa la siembra aunque el archivo `.db` ya exista |
| Archivo `.db` | No existía antes del arranque | `firstRun = true` → siembra obligatoria |
| Timer `_timer` | No debe estar ya instanciado | `NightlyBatch.Start()` hace guard `if (_timer != null) return` |

---

## 3. Componentes

### Backend

| Componente | Propósito | Responsabilidades Clave |
|-----------|-----------|------------------------|
| `Global.asax` (`Application_Start`) | Punto de entrada del ciclo de vida ASP.NET | Llama a `EnsureDatabase` y `NightlyBatch.Start`; no contiene lógica propia |
| `LegacyRepository.EnsureDatabase()` | Inicialización idempotente de la BD | Crea directorio, ejecuta DDL, decide si sembrar datos |
| `LegacyRepository.SeedDatabase()` | Siembra de datos de muestra | Inserta clientes, pedidos y registro de auditoría `BOOTSTRAP` |
| `NightlyBatch.Start()` | Armado del temporizador periódico | Lee intervalo de config, crea `System.Threading.Timer` |
| `LegacyState.SyncRoot` | Coordinación de concurrencia | Lock compartido entre `EnsureDatabase`, `Load` y `Save` |

### Frontend

No hay componentes de interfaz de usuario involucrados en este submódulo.

### Datos

| Entidad | Propósito | Campos Principales |
|---------|-----------|-------------------|
| `Customers` | Ficha de crédito del cliente | `Id`, `Name`, `Country`, `Since`, `CreditLimit`, `OutstandingDebt`, `OverdueDays`, `RiskScore`, `Blocked`, `ManualOverride` |
| `Orders` | Pedidos con resultado de decisión crediticia | `Id`, `CustomerId`, `Amount`, `PaymentMethod`, `Priority`, `Status`, `DecisionCode`, `DecisionText`, `CreatedAt` |
| `Audit` | Registro de eventos del sistema | `Id`, `At`, `Type`, `Subject`, `Detail` |
| `vw_CustomerExposure` | Exposición total por cliente (deuda + pedidos abiertos) | `Id`, `Name`, `CreditLimit`, `OutstandingDebt`, `OpenOrders`, `TotalExposure` |
| `vw_ManualReviewQueue` | Cola de pedidos en revisión manual | `Id`, `CustomerId`, `Name`, `Amount`, `DecisionCode`, `DecisionText`, `CreatedAt` |

---

## 4. APIs y Endpoints

### Endpoints Principales

Este submódulo no expone endpoints HTTP propios. Es llamado exclusivamente por el runtime ASP.NET antes de que el servidor HTTP esté disponible para el tráfico.

| Mecanismo | Disparador | Propósito | Autenticación |
|-----------|-----------|-----------|---------------|
| `Application_Start` (evento ASP.NET) | Primera petición HTTP recibida | Bootstrapear BD y temporizador | No aplica (runtime interno) |

---

## 5. Flujos de Datos

### Flujo Principal: Arranque de la Aplicación

```
Runtime ASP.NET
    → Application_Start (Global.asax:3)
        → LegacyRepository.EnsureDatabase()
            → lock(LegacyState.SyncRoot)
            → Directory.CreateDirectory (si App_Data no existe)
            → SqliteConnection.Open() + PRAGMA foreign_keys=ON; busy_timeout=5000
            → Ejecutar SchemaSql (DDL idempotente: tablas, índices, vistas, trigger)
            → if (firstRun || COUNT(Customers)==0) → SeedDatabase()
                → INSERT OR IGNORE: 5 clientes, 2 pedidos, 1 registro BOOTSTRAP
                → tx.Commit()
        → NightlyBatch.Start()
            → Leer BatchIntervalSeconds de web.config (default 45, mínimo 10)
            → new Timer(Tick, null, interval, interval)
    → Aplicación lista para servir peticiones HTTP
```

**Pasos resumidos**:
1. El runtime dispara el evento antes del primer request.
2. Se garantiza la existencia del archivo `.db` con esquema completo.
3. Se insertan datos semilla si la BD está vacía.
4. Se inicia el temporizador de procesamiento por lotes.
5. La aplicación queda operativa.

### Manejo de Errores

| Error | Causa | Comportamiento |
|-------|-------|----------------|
| Excepción en `EnsureDatabase` | Fallo de E/S, permisos en `App_Data`, SQLite corrupto | Burbujea sin capturar → fallo duro de arranque de la aplicación; ningún request puede ser servido |
| Excepción en `NightlyBatch.Start` | Imposible crear `Timer` (OOM u otro) | Burbujea sin capturar → fallo duro de arranque |
| `BatchIntervalSeconds` inválido o ausente | Valor no numérico o clave ausente en `web.config` | `Int32.TryParse` falla silenciosamente; se usa el default 45 s; si < 10 se aplica mínimo 10 s |
| Directorio `App_Data` no creado | Sin permisos de escritura en el sistema de archivos | `Directory.CreateDirectory` lanza `IOException` → fallo duro de arranque |

---

## 6. Integraciones

### Internas

- **Data Access and Persistence Layer** (`LegacyRepository`): Delega la creación de esquema y siembra. Intercambia el path de la BD (resuelto vía `HttpRuntime.AppDomainAppPath`) y el lock `LegacyState.SyncRoot`.
- **Nightly Batch Processing** (`NightlyBatch`): Recibe la señal de inicio del temporizador. No se intercambia ningún dato en esta fase; `NightlyBatch.Start()` solo instancia el timer.
- **Global In-Memory State** (`LegacyState`): Proporciona `SyncRoot` para serializar el acceso a la BD durante el arranque. `BatchExecutions`, `LastBatchRun` y `LastBatchMessage` se inicializan con sus valores estáticos por defecto (0, `DateTime.MinValue`, `"Never executed"`).
- **Application Shutdown Handler** (mismo archivo `Global.asax`): El handler de `Application_End` es el único que llama a `NightlyBatch.Stop()` para liberar el timer creado aquí.

### Externas

No hay integraciones con sistemas externos en este submódulo.

---

## 7. Configuración

| Propiedad | Propósito | Valor por Defecto |
|-----------|-----------|-------------------|
| `BatchIntervalSeconds` | Intervalo (segundos) entre ejecuciones del batch nocturno | `15` (en `web.config` de este fixture; código usa 45 si ausente) |
| `App_Data/legacy-credit-desk.db` | Ruta del archivo SQLite (derivada, no configurable directamente) | `<AppDomainAppPath>/App_Data/legacy-credit-desk.db` |
| `PRAGMA foreign_keys=ON` | Habilita integridad referencial en SQLite | Siempre activo (fijado en `OpenConnection`) |
| `PRAGMA busy_timeout=5000` | Tiempo de espera (ms) ante bloqueos de escritura concurrentes | 5000 ms (fijado en código) |

---

## 8. Resumen

- **Componentes**: 5 backend, 0 frontend, 5 entidades de datos (3 tablas, 2 vistas + 1 trigger)
- **Endpoints**: 0 (no expone endpoints HTTP)
- **Reglas de negocio**: 5 (creación idempotente de esquema, siembra condicional, intervalo mínimo de batch, lock de concurrencia, registro BOOTSTRAP en auditoría)
- **Integraciones**: 4 internas (LegacyRepository, NightlyBatch, LegacyState, Application Shutdown Handler), 0 externas

---
**Nota**: Documento generado automáticamente por CodeToDocAnalyzer. Mantener bajo 1500 líneas. Priorizar información funcional esencial.
