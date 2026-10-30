# Especificación Funcional: Inicialización de Base de Datos y Gestión de Esquema - Componente Backend

**Módulo Padre**: Data Access and Persistence Layer | **Fecha**: 2026-08-10

---

## 1. Resumen Ejecutivo

- **Propósito**: Garantizar la existencia y coherencia del esquema SQLite antes de cualquier operación de datos. Crea el fichero de base de datos, ejecuta el DDL completo y carga los datos iniciales en el primer arranque.
- **Funcionalidad Clave**:
  - Creación bajo demanda del fichero `legacy-credit-desk.db` en `App_Data/`
  - Ejecución idempotente del DDL completo (tablas, índices, vistas, trigger)
  - Protección ante condiciones de carrera mediante bloqueo global (`LegacyState.SyncRoot`)
  - Configuración de pragmas SQLite (`foreign_keys=ON`, `busy_timeout=5000`) en cada conexión
  - Llamada automática a la siembra de datos cuando la tabla `Customers` está vacía
- **Usuarios**: No tiene interfaz de usuario directa. Es invocado automáticamente por el arranque de la aplicación (`Global.asax`) y defensivamente por todos los métodos públicos del repositorio.

---

## 2. Lógica de Negocio

### Procesos Clave

**Inicialización de base de datos (`EnsureDatabase`)**: Punto de entrada único para asegurar que el esquema existe antes de cualquier operación.

1. Adquiere el bloqueo global `LegacyState.SyncRoot` (exclusión mutua entre hilos).
2. Crea el directorio `App_Data/` si no existe.
3. Detecta si es el primer arranque (`!File.Exists(DbPath)`).
4. Abre conexión y ejecuta `SchemaSql` con `CREATE TABLE/INDEX/VIEW/TRIGGER IF NOT EXISTS` (operación idempotente).
5. Si es primer arranque o `COUNT(*) FROM Customers == 0`, invoca `SeedDatabase()`.

**Apertura de conexión (`OpenConnection`)**: Garantiza configuración uniforme en cada conexión.

1. Abre `SqliteConnection` con la cadena de conexión `URI=file:<ruta>;Version=3`.
2. Ejecuta `PRAGMA foreign_keys=ON` — activa integridad referencial.
3. Ejecuta `PRAGMA busy_timeout=5000` — espera hasta 5 segundos ante bloqueos de escritura.

### Validaciones Críticas

| Campo | Validación | Comportamiento |
|-------|------------|----------------|
| Directorio `App_Data/` | Debe existir antes de abrir la BD | Se crea automáticamente si falta |
| Fichero `legacy-credit-desk.db` | Detectado por `File.Exists` | Primer arranque activa siembra |
| Tabla `Customers` | `COUNT(*) == 0` | Activa siembra aunque el fichero ya exista |
| DDL (`SchemaSql`) | `IF NOT EXISTS` en todos los objetos | Ejecución segura en arranques sucesivos |

---

## 3. Componentes

### Backend

| Componente | Propósito | Responsabilidades Clave |
|-----------|-----------|------------------------|
| `LegacyRepository.EnsureDatabase()` (línea 24) | Punto de entrada de inicialización | Crear directorio/fichero, ejecutar DDL, invocar siembra |
| `LegacyRepository.OpenConnection()` (línea 123) | Fábrica de conexiones | Abrir `SqliteConnection`, aplicar PRAGMAs |
| `LegacyRepository.DbPath` (línea 10) | Resolución de ruta física | Combina `HttpRuntime.AppDomainAppPath` + `App_Data/legacy-credit-desk.db` |
| `LegacyRepository.ConnectionString` (línea 19) | Cadena de conexión | Genera `URI=file:<ruta>;Version=3` |
| `LegacyRepository.SchemaSql` (línea 352) | DDL completo embebido | Define tablas, índices, vistas y trigger |
| `LegacyRepository.ScalarInt()` (línea 271) | Consulta escalar auxiliar | Ejecuta SQL y devuelve `int`; usado para `COUNT(*) FROM Customers` |

### Frontend

*No aplica — este subcomponente no tiene interfaz de usuario.*

### Datos

| Entidad | Propósito | Campos Principales |
|---------|-----------|-------------------|
| `Customers` | Ficha de crédito de clientes | `Id` (PK), `Name`, `Country`, `Since`, `CreditLimit`, `OutstandingDebt`, `OverdueDays`, `RiskScore`, `Blocked`, `ManualOverride` |
| `Orders` | Pedidos con resultado de decisión | `Id` (PK), `CustomerId` (FK), `Amount`, `PaymentMethod`, `Priority`, `Status`, `DecisionCode`, `DecisionText`, `CreatedAt` |
| `Audit` | Registro de eventos del sistema | `Id` (PK), `At`, `Type`, `Subject`, `Detail` |
| `vw_CustomerExposure` | Vista de exposición total por cliente | `Id`, `Name`, `CreditLimit`, `OutstandingDebt`, `OpenOrders`, `TotalExposure` |
| `vw_ManualReviewQueue` | Vista de pedidos en revisión manual | `Id`, `CustomerId`, `Name`, `Amount`, `DecisionCode`, `DecisionText`, `CreatedAt` |
| `trg_OrderBlockedAudit` | Trigger de auditoría DB-level | Inserta en `Audit` automáticamente al insertar un pedido con `Status='BLOCKED'` |

---

## 4. APIs y Endpoints

### Endpoints Principales

*Este subcomponente no expone endpoints HTTP. Es una capa de infraestructura interna.*

| Método Interno | Firma | Propósito | Sincronización |
|---------------|-------|-----------|---------------|
| `EnsureDatabase()` | `public static void` | Inicializar esquema y datos | `LegacyState.SyncRoot` |
| `OpenConnection()` | `private static SqliteConnection` | Crear conexión configurada | — |
| `ScalarInt(string sql)` | `private static int` | Consulta escalar de control | — |

**Ejemplo de llamada al arranque**:
```csharp
// Global.asax — Application_Start
void Application_Start(object sender, EventArgs e)
{
    LegacyRepository.EnsureDatabase();  // garantiza esquema antes de servir páginas
    NightlyBatch.Start();
}
```

**DDL embebido — objetos creados**:
```sql
-- Tablas
CREATE TABLE IF NOT EXISTS Customers(Id TEXT PRIMARY KEY, ...)
CREATE TABLE IF NOT EXISTS Orders(Id TEXT PRIMARY KEY, ..., FOREIGN KEY(CustomerId) REFERENCES Customers(Id))
CREATE TABLE IF NOT EXISTS Audit(Id TEXT PRIMARY KEY, ...)

-- Índices
CREATE INDEX IF NOT EXISTS IX_Orders_Customer_Status ON Orders(CustomerId, Status)
CREATE INDEX IF NOT EXISTS IX_Orders_CreatedAt ON Orders(CreatedAt)

-- Vistas
CREATE VIEW IF NOT EXISTS vw_CustomerExposure AS ...
CREATE VIEW IF NOT EXISTS vw_ManualReviewQueue AS ...

-- Trigger
CREATE TRIGGER IF NOT EXISTS trg_OrderBlockedAudit AFTER INSERT ON Orders WHEN NEW.Status='BLOCKED' ...
```

---

## 5. Flujos de Datos

### Flujo Principal: Inicialización en Arranque

```
Global.asax.Application_Start
    → LegacyRepository.EnsureDatabase()
        → lock(LegacyState.SyncRoot)
        → Directory.CreateDirectory(App_Data/) [si no existe]
        → OpenConnection() → PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000
        → ExecuteNonQuery(SchemaSql)  [CREATE IF NOT EXISTS — idempotente]
        → ScalarInt("SELECT COUNT(*) FROM Customers")
            → si == 0: SeedDatabase()
        → unlock
```

**Pasos**:
1. `Application_Start` invoca `EnsureDatabase()` antes de servir ninguna petición HTTP.
2. Se adquiere el bloqueo global para evitar inicializaciones concurrentes.
3. Se ejecuta el DDL completo; los `IF NOT EXISTS` hacen la operación segura en reinicios.
4. Se comprueba si la tabla `Customers` está vacía para decidir si sembrar datos.
5. El bloqueo se libera; el repositorio queda listo para uso.

### Flujo Defensivo: Llamada desde Métodos Públicos

```
LegacyRepository.Load() / Save() / SumOpenOrders() / CountRecentOrders()
    → EnsureDatabase()  [llamada defensiva en cada método público]
    → (ya inicializado → no-op en ejecuciones posteriores)
```

### Manejo de Errores

| Error | Causa | Comportamiento |
|-------|-------|----------------|
| `DirectoryNotFoundException` al crear fichero | `App_Data/` no existe | Se crea automáticamente (`Directory.CreateDirectory`) |
| `SqliteException` en DDL | Fallo de I/O o permisos | Excepción no capturada — propaga al arranque de ASP.NET, impidiendo que la aplicación arranque |
| BD vacía tras primer arranque | `Customers` sin filas | `SeedDatabase()` repuebla la BD en la misma transacción de inicialización |
| Contención de hilos | Múltiples peticiones simultáneas en el arranque | `LegacyState.SyncRoot` serializa el acceso; sólo un hilo ejecuta el DDL |
| Busy lock de SQLite | Escritura concurrente desde otro proceso | `PRAGMA busy_timeout=5000` reintenta durante 5 s antes de lanzar excepción |

---

## 6. Integraciones

### Internas

- **Application Bootstrap (`Global.asax`)**: Llama a `EnsureDatabase()` en `Application_Start` — garantiza que el esquema exista antes de servir páginas.
- **Seed Data and Database Bootstrap (`SeedDatabase`)**: Invocado desde `EnsureDatabase()` cuando `Customers` está vacía — proporciona el estado inicial de referencia.
- **Global In-Memory State (`LegacyState.SyncRoot`)**: Proporciona el mutex global que protege la inicialización frente a condiciones de carrera.
- **Todos los métodos públicos de `LegacyRepository`**: Llaman defensivamente a `EnsureDatabase()` antes de abrir cualquier conexión.

### Externas

- **`Mono.Data.Sqlite`**: Driver ADO.NET para SQLite sobre Mono — único punto de acceso a la base de datos física.
- **`System.Web.HttpRuntime`**: Proporciona `AppDomainAppPath` para resolver la ruta absoluta al fichero de BD.

---

## 7. Configuración

| Propiedad | Propósito | Valor / Default |
|-----------|-----------|-----------------|
| Ruta física BD | Ubicación del fichero SQLite | `<AppDomainAppPath>/App_Data/legacy-credit-desk.db` |
| `URI=file:<ruta>;Version=3` | Cadena de conexión SQLite | Construida dinámicamente desde `DbPath` |
| `PRAGMA foreign_keys=ON` | Activa integridad referencial en cada conexión | Siempre activado (no configurable) |
| `PRAGMA busy_timeout=5000` | Tiempo de espera (ms) ante bloqueos SQLite | 5 000 ms (fijo en código) |
| `LegacyState.SyncRoot` | Objeto de bloqueo global | Instancia estática compartida con todos los subcomponentes del repositorio |

---

## 8. Resumen

- **Componentes**: 6 backend, 0 frontend, 5 entidades de datos (3 tablas, 2 vistas, 1 trigger)
- **Endpoints HTTP**: 0 (infraestructura interna)
- **Reglas de negocio**: 4 (creación de directorio, idempotencia DDL, detección primer arranque, siembra condicional)
- **Integraciones**: 4 internas (Bootstrap, Seed, State, métodos del repositorio), 2 externas (Mono.Data.Sqlite, HttpRuntime)

---

**Nota**: Documento < 1500 líneas. Información funcional esencial priorizada. El DDL completo está embebido en `LegacyRepository.SchemaSql` (líneas 352–379 de `App_Code/LegacyRepository.cs`).
