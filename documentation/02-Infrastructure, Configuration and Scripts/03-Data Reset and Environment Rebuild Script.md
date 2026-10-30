# Especificación Funcional: Script de Reinicio de Datos y Reconstrucción del Entorno

**Módulo Padre**: Infrastructure, Configuration and Scripts | **Fecha**: 2026-08-10

## 1. Resumen Ejecutivo

- **Propósito**: Proporcionar un mecanismo de un único comando para destruir completamente el entorno Docker en ejecución (incluyendo el volumen de datos persistido) y reconstruirlo desde cero, retornando la aplicación a un estado limpio y conocido.
- **Funcionalidad Clave**:
  - Derribo total del contenedor y eliminación del volumen Docker nombrado (`legacy-credit-data`)
  - Reconstrucción de la imagen Docker desde el `Dockerfile`
  - Reinicio del servicio con datos iniciales regenerados automáticamente
  - Recreación del esquema SQLite y datos semilla mediante `LegacyRepository.EnsureDatabase()` en el primer arranque
  - Garantía de idempotencia: siempre produce el mismo estado inicial independientemente del estado previo
- **Usuarios**: Desarrolladores y técnicos de pruebas con acceso a la línea de comandos del host y al entorno Docker

---

## 2. Lógica de Negocio

### Procesos Clave

**Proceso de Reinicio Completo**: Destrucción y reconstrucción del entorno desde cero
1. El operador ejecuta `bash scripts/reset-data.sh` desde el directorio raíz del proyecto
2. `docker compose down -v` detiene todos los contenedores del servicio y elimina el volumen nombrado `legacy-credit-data` (junto con el fichero `legacy-credit-desk.db` que contiene)
3. `docker compose up --build` reconstruye la imagen Docker desde el `Dockerfile` e inicia el contenedor
4. Al recibir la primera petición HTTP, `Global.asax` invoca `LegacyRepository.EnsureDatabase()`
5. `EnsureDatabase()` detecta que no existe el fichero de base de datos, ejecuta el DDL (`SchemaSql`) y llama a `SeedDatabase()` para insertar los datos iniciales

**Proceso de Recreación de Esquema y Datos Semilla** (ejecutado por `LegacyRepository.EnsureDatabase()`):
1. Crea tablas `Customers`, `Orders`, `Audit` con DDL idempotente (`CREATE TABLE IF NOT EXISTS`)
2. Crea índices `IX_Orders_Customer_Status`, `IX_Orders_CreatedAt`
3. Crea vistas `vw_CustomerExposure` y `vw_ManualReviewQueue`
4. Crea trigger `trg_OrderBlockedAudit`
5. Inserta 5 clientes semilla y 2 pedidos semilla en una transacción única; escribe un registro de auditoría `SYSTEM/BOOTSTRAP`

### Validaciones Críticas

| Campo / Condición | Validación | Comportamiento |
|---|---|---|
| Presencia de `xsp4` en imagen | Instalado vía apt en el `Dockerfile` | Fallo de build si el paquete no está disponible |
| Existencia del fichero DB | `!File.Exists(DbPath)` en `EnsureDatabase()` | Si no existe → ejecuta DDL + semilla |
| Tabla `Customers` vacía | `COUNT(*) == 0` en `EnsureDatabase()` | Si vacía aunque el fichero exista → vuelve a sembrar |
| Salida de comandos Docker | `set -euo pipefail` en el script | Aborta inmediatamente ante cualquier error de Docker |
| Claves duplicadas en semilla | `INSERT OR IGNORE` en `InsertCustomer` / `InsertOrder` | Evita duplicados en reinicios parciales |

---

## 3. Componentes

### Backend

| Componente | Propósito | Responsabilidades Clave |
|---|---|---|
| `scripts/reset-data.sh` | Orquestador del reinicio | Ejecutar `docker compose down -v` y `docker compose up --build`; abortar ante error (`set -euo pipefail`) |
| `LegacyRepository.EnsureDatabase()` | Inicialización de la base de datos | Crear esquema SQLite, detectar primera ejecución, invocar `SeedDatabase()` |
| `LegacyRepository.SeedDatabase()` | Inserción de datos iniciales | Insertar 5 clientes y 2 pedidos semilla en transacción; escribir registro de auditoría de arranque |
| `Global.asax` (Application_Start) | Disparador de inicialización | Llamar a `LegacyRepository.EnsureDatabase()` en el arranque de la aplicación ASP.NET |

### Frontend

| Componente | Propósito | Funcionalidad Principal |
|---|---|---|
| (Ninguno) | Este submódulo no expone interfaz de usuario | El reinicio se opera exclusivamente desde la línea de comandos del host |

### Datos

| Entidad | Propósito | Campos Principales |
|---|---|---|
| `Customers` (tabla SQLite) | Perfiles de crédito de clientes | `Id`, `Name`, `Country`, `Since`, `CreditLimit`, `OutstandingDebt`, `OverdueDays`, `RiskScore`, `Blocked`, `ManualOverride` |
| `Orders` (tabla SQLite) | Pedidos y resultados de decisión crediticia | `Id`, `CustomerId`, `Amount`, `PaymentMethod`, `Priority`, `Status`, `DecisionCode`, `DecisionText`, `CreatedAt` |
| `Audit` (tabla SQLite) | Registro de auditoría del sistema | `Id`, `At`, `Type`, `Subject`, `Detail` |
| `vw_CustomerExposure` (vista) | Exposición total por cliente | `Id`, `Name`, `CreditLimit`, `OutstandingDebt`, `OpenOrders`, `TotalExposure` |
| `vw_ManualReviewQueue` (vista) | Cola de pedidos en revisión manual | `Id`, `CustomerId`, `Name`, `Amount`, `DecisionCode`, `DecisionText`, `CreatedAt` |
| `trg_OrderBlockedAudit` (trigger) | Auditoría automática de pedidos bloqueados | Se dispara en `INSERT` sobre `Orders` cuando `Status='BLOCKED'` |
| Volumen Docker `legacy-credit-data` | Persistencia del fichero SQLite entre reinicios de contenedor | Montado en `/app/App_Data`; destruido por `down -v` |

---

## 4. APIs y Endpoints

### Endpoints Principales

| Método | URL / Comando | Propósito | Auth |
|---|---|---|---|
| (CLI) | `bash scripts/reset-data.sh` | Reinicio completo del entorno | Acceso shell al host |
| (Interno) | `LegacyRepository.EnsureDatabase()` | Inicialización del esquema y semilla | Llamada interna en arranque |

Este submódulo no expone endpoints HTTP propios. Su efecto es indirecto: tras el reinicio, todos los endpoints de la aplicación (`Default.aspx`, `Customers.aspx`, `OrderEntry.aspx`, `Batch.aspx`) vuelven a estar disponibles en `http://localhost:8080` con datos limpios.

**Ejemplo de ejecución**:
```sh
# Desde el directorio raíz del proyecto en el host
bash scripts/reset-data.sh
# Salida esperada:
# [+] Running 2/2 — network y volumen eliminados
# [+] Building ... — imagen reconstruida
# [+] Running 1/1 — contenedor iniciado
```

---

## 5. Flujos de Datos

### Flujo Principal: Reinicio Completo del Entorno

```
Operador (CLI)
    → bash scripts/reset-data.sh
    → docker compose down -v
        → Contenedor detenido
        → Volumen legacy-credit-data eliminado (legacy-credit-desk.db destruido)
    → docker compose up --build
        → Dockerfile: imagen reconstruida (mono:6.12 + xsp4 + sqlite3)
        → Contenedor iniciado; App_Data vacío
    → Primera petición HTTP
        → Global.asax: Application_Start
        → LegacyRepository.EnsureDatabase()
            → CREATE TABLE/INDEX/VIEW/TRIGGER (idempotente)
            → SeedDatabase(): 5 clientes + 2 pedidos + 1 auditoría BOOTSTRAP
        → Aplicación lista con datos semilla
```

**Pasos clave**:
1. `down -v` garantiza la destrucción del estado anterior (sin residuos de datos)
2. `--build` fuerza la reconstrucción de la imagen (incluye cambios en `Dockerfile` o código fuente)
3. La recreación del esquema es delegada completamente a `LegacyRepository` — el script no toca SQL directamente
4. `EnsureDatabase()` es idempotente: usa `CREATE ... IF NOT EXISTS` e `INSERT OR IGNORE`
5. El estado efímero en memoria (`LegacyState`) se reinicia al no ser persistido

### Manejo de Errores

| Error | Causa | Acción |
|---|---|---|
| Fallo en `docker compose down` | Docker no disponible o permisos insuficientes | `set -euo pipefail` aborta el script; no se ejecuta el `up` |
| Fallo en `docker compose up --build` | Error de construcción de imagen (apt, Dockerfile) | Script termina con código de error no-cero |
| `App_Data` sin permisos de escritura | `chmod 777 /app/App_Data` no aplicado en Dockerfile | `EnsureDatabase()` lanza excepción de I/O en arranque |
| Claves duplicadas en semilla | Reinicio parcial previo dejó datos | `INSERT OR IGNORE` descarta duplicados silenciosamente |
| `xsp4` no disponible en imagen | Paquete `mono-xsp4` no instalable (mirror de archivo) | Fallo en `docker build`; imagen no se crea |

---

## 6. Integraciones

### Internas

- **Container Build and Deployment** (`Dockerfile` + `docker-compose.yml`): El script delega completamente en estos artefactos para la definición del entorno. El `docker-compose.yml` define el volumen `legacy-credit-data` que es destruido por `down -v`.
- **Data Access and Persistence Layer** (`LegacyRepository.EnsureDatabase()`, `SeedDatabase()`): Responsable de recrear el esquema SQLite y los datos semilla tras el reinicio. El script no interactúa directamente con SQL.
- **Application Bootstrap** (`Global.asax`): Dispara `EnsureDatabase()` en el primer arranque post-reinicio.
- **Smoke Test Script** (`scripts/verify-docker.sh`): Puede ejecutarse tras el reinicio para verificar que el entorno reconstruido es funcional.

### Externas

- **Docker Engine**: Dependencia obligatoria del host. Ambos comandos (`down -v`, `up --build`) requieren el daemon Docker activo y accesible desde la shell del operador.
- **Docker Compose**: Plugin o binario independiente de Docker Compose (v2 recomendado, sintaxis `docker compose` sin guion).
- **archive.debian.org**: Repositorio APT de archivo usado en el `Dockerfile` para instalar `mono-xsp4` y `sqlite3` dado que Debian Buster (base de `mono:6.12`) está EOL.

---

## 7. Configuración

| Propiedad | Propósito | Valor / Default |
|---|---|---|
| `legacy-credit-data` (volumen Docker) | Nombre del volumen nombrado que persiste `App_Data`; destruido por `down -v` | Definido en `docker-compose.yml` |
| `/app/App_Data` (ruta en contenedor) | Directorio de montaje del volumen; debe tener permisos `777` | Configurado en `Dockerfile` (`chmod 777`) |
| Puerto `8080` | Puerto expuesto por el contenedor post-reinicio | Definido en `docker-compose.yml` y `Dockerfile` (`EXPOSE 8080`) |
| `restart: unless-stopped` | Política de reinicio del servicio; el contenedor se reinicia solo salvo parada explícita | Definido en `docker-compose.yml` |
| `set -euo pipefail` | Modo de ejecución segura del script; cualquier error aborta inmediatamente | Hardcoded en `scripts/reset-data.sh` |
| Datos semilla (hardcoded) | 5 clientes y 2 pedidos fijos insertados en `LegacyRepository.SeedDatabase()` | Definidos en `App_Code/LegacyRepository.cs` |

---

## 8. Resumen

- **Componentes**: 1 script shell (`reset-data.sh`), 2 métodos backend (`EnsureDatabase`, `SeedDatabase`), 1 artefacto de arranque (`Global.asax`)
- **Endpoints**: 0 (operación exclusivamente CLI)
- **Reglas de negocio**: 3 (derribo total con `-v`, detección de primera ejecución por ausencia de fichero DB, resiembra si tabla `Customers` vacía)
- **Integraciones**: 4 internas (Container Build, Data Access Layer, App Bootstrap, Smoke Test), 3 externas (Docker Engine, Docker Compose, archive.debian.org)

---
**Nota**: Documento generado a partir del análisis de `scripts/reset-data.sh`, `App_Code/LegacyRepository.cs`, `Dockerfile` y `docker-compose.yml`. Mantener documento < 1500 líneas. Priorizar información funcional esencial.
