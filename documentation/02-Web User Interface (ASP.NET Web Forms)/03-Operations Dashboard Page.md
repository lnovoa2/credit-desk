# Especificación Funcional: Operations Dashboard Page

**Módulo Padre**: Web User Interface (ASP.NET Web Forms) | **Fecha**: 2026-08-10

## 1. Resumen Ejecutivo

- **Propósito**: Página de inicio de la aplicación (`Default.aspx`) que ofrece una vista de solo lectura del estado operativo actual del sistema de crédito. Agrega métricas clave de negocio y presenta las decisiones crediticias y entradas de auditoría más recientes en formato tabular.
- **Funcionalidad Clave**:
  - Calcula y muestra cuatro KPIs operativos desde el `DataSet` en memoria: total de clientes, clientes bloqueados, órdenes en revisión manual y deuda total pendiente.
  - Presenta las 8 órdenes más recientes ordenadas de más nueva a más antigua.
  - Presenta las 8 entradas de auditoría más recientes ordenadas de más nueva a más antigua.
  - No realiza ninguna operación de escritura; es una página de solo lectura.
- **Usuarios**: Operadores de crédito y supervisores que necesitan una visión rápida del estado del sistema.

---

## 2. Lógica de Negocio

### Procesos Clave

**Carga y agregación de KPIs**: Al cargar la página (solo en peticiones no-postback), se obtiene el `DataSet` completo desde `LegacyRepository.Load()` y se calculan los cuatro indicadores operativos.

1. Invocar `LegacyRepository.Load()` para obtener el `DataSet` con las tablas `Customers`, `Orders` y `Audit`.
2. Contar clientes bloqueados: `DataTable.Select("Blocked=true").Length` sobre la tabla `Customers`.
3. Contar órdenes en revisión manual: `DataTable.Select("Status='REVIEW'").Length` sobre la tabla `Orders`.
4. Sumar la deuda pendiente total: iteración `for` sobre todas las filas de `Customers` acumulando el campo `OutstandingDebt` (cálculo en memoria, no SQL).
5. Renderizar los KPIs como HTML inline en el control `<asp:Literal ID="Kpis">` con la clase CSS `.kpi`.

**Presentación de tablas de actividad reciente**: Se obtienen las últimas N filas de las tablas de órdenes y auditoría mediante el método privado `Tail()`.

1. Invocar `Tail(ds.Tables["Orders"], 8)` para extraer las 8 órdenes más recientes en orden inverso.
2. Invocar `Tail(ds.Tables["Audit"], 8)` para extraer las 8 entradas de auditoría más recientes en orden inverso.
3. Vincular los resultados a los controles `GridView` (`OrdersGrid`, `AuditGrid`) con `AutoGenerateColumns="true"`.

### Validaciones Críticas

| Campo | Validación | Comportamiento |
|-------|------------|----------------|
| `IsPostBack` | Si la petición es un postback, se omite toda la carga de datos | No hay procesamiento en postback (página de solo lectura sin formularios activos) |

### Algoritmo `Tail(DataTable, int count)`

Extrae las últimas `count` filas de un `DataTable` en orden inverso (más reciente primero):

1. Crear una copia del esquema del `DataTable` fuente con `source.Clone()`.
2. Calcular el índice de inicio: `start = source.Rows.Count - count` (mínimo 0).
3. Iterar desde el último índice hacia `start`, importando cada fila con `ImportRow()`.
4. Devolver el `DataTable` resultado con filas en orden descendente.

> **Nota de deuda técnica**: Este método es idéntico al implementado en `OrderEntry.aspx.cs`. Es un candidato claro de consolidación en una clase de utilidades compartida durante cualquier migración.

---

## 3. Componentes

### Backend

| Componente | Propósito | Responsabilidades Clave |
|-----------|-----------|------------------------|
| `Default.aspx.cs` (`DashboardPage`) | Code-behind de la página de dashboard | `Page_Load`: carga datos, calcula KPIs, vincula grids; `Tail()`: extrae últimas N filas en orden inverso |

### Frontend

| Componente | Propósito | Funcionalidad Principal |
|-----------|-----------|------------------------|
| `Default.aspx` | Markup de la página de dashboard | Declara el control `<asp:Literal ID="Kpis">` para tiles de KPI y dos `<asp:GridView>` (`OrdersGrid`, `AuditGrid`) para datos tabulares |
| `Site.master` | Layout maestro compartido | Provee la cabecera, barra de navegación y `ContentPlaceHolder` que `Default.aspx` hereda |

### Datos

| Entidad | Propósito | Campos Principales |
|---------|-----------|-------------------|
| `Customers` | Fuente para KPIs de clientes | `Id`, `Blocked` (filtro KPI), `OutstandingDebt` (suma KPI) |
| `Orders` | Fuente para KPI de revisiones y grid de órdenes | `Status` (filtro `='REVIEW'`), `Id`, `CustomerId`, `Amount`, `PaymentMethod`, `Priority`, `DecisionCode`, `DecisionText`, `CreatedAt` |
| `Audit` | Fuente para grid de auditoría | `Id`, `At`, `Type`, `Subject`, `Detail` |

---

## 4. APIs y Endpoints

### Endpoints Principales

| Método | URL | Propósito | Auth |
|--------|-----|-----------|------|
| GET | `/Default.aspx` | Cargar el dashboard de operaciones (página de inicio) | No (autenticación no implementada en este proyecto) |

> Esta página no expone ningún endpoint REST ni servicio web. Es una página Web Forms clásica que responde exclusivamente a peticiones GET. No hay botones ni formularios que generen postbacks funcionales.

**Ejemplo de flujo de datos HTTP**:
```
// Request
GET /Default.aspx HTTP/1.1

// Response: HTML renderizado con KPIs y grids, sin JSON ni API intermedia
HTTP/1.1 200 OK
Content-Type: text/html; charset=utf-8
```

---

## 5. Flujos de Datos

### Flujo Principal: Carga del Dashboard

```
Navegador → GET /Default.aspx → Page_Load (no postback)
    → LegacyRepository.Load() → SQLite (Customers, Orders, Audit)
    → Cálculo KPIs en memoria → Render HTML Kpis Literal
    → Tail(Orders, 8) → OrdersGrid.DataBind()
    → Tail(Audit, 8) → AuditGrid.DataBind()
    → Respuesta HTML completa al navegador
```

**Pasos**:
1. El navegador realiza una petición GET a `/Default.aspx`.
2. ASP.NET instancia `DashboardPage` y dispara `Page_Load`; se descarta si es postback.
3. `LegacyRepository.Load()` adquiere el lock sobre `LegacyState.SyncRoot`, abre la conexión SQLite y llena el `DataSet` con las tres tablas.
4. Se calculan los cuatro KPIs en memoria y se genera el HTML de los tiles.
5. `Tail()` extrae las últimas filas de `Orders` y `Audit`; los grids se vinculan y renderizan.
6. ASP.NET devuelve la respuesta HTML completa.

### Manejo de Errores

| Error | Causa | Acción |
|-------|-------|--------|
| Excepción en `LegacyRepository.Load()` | Base de datos SQLite inaccesible o corrupta | La excepción no capturada se propaga y ASP.NET muestra una página de error genérica del servidor |
| `DataTable` vacío (`Orders` o `Audit`) | Sin datos en la BD (primer arranque antes del seed) | `Tail()` devuelve una tabla vacía; el GridView se renderiza sin filas, sin error visible |

---

## 6. Integraciones

### Internas

- **Data Access and Persistence Layer (`LegacyRepository`)**: Única dependencia de datos. Se invoca `LegacyRepository.Load()` para obtener el `DataSet` completo. La página nunca llama a `Save()`.
- **Master Page Layout and Navigation (`Site.master`)**: Provee el contenedor HTML, la cabecera de aplicación, la barra de navegación y la hoja de estilos. `Default.aspx` hereda de `Site.master` mediante la directiva `MasterPageFile="~/Site.master"`.
- **Application Stylesheet (`Content/site.css`)**: La clase CSS `.kpi` define el estilo visual de los tiles de KPI generados inline en `Kpis.Text`. Los controles GridView usan la clase `.grid`.

### Externas

- Ninguna. La página no consume servicios externos, APIs de terceros ni sistemas fuera de la aplicación.

---

## 7. Configuración

| Propiedad | Propósito | Default |
|-----------|-----------|---------|
| `AutoGenerateColumns="true"` (GridView) | Los grids muestran automáticamente todas las columnas del `DataTable` vinculado, sin configuración explícita de columnas | `true` (hardcoded en markup) |
| Número de filas en grids (`count=8`) | Cantidad de filas recientes mostradas en `OrdersGrid` y `AuditGrid` | `8` (hardcoded en `Default.aspx.cs:Page_Load`) |
| Nota de versión en cabecera | Texto informativo `"Internal build 2.7.14"` visible en la página | `2.7.14` (hardcoded en `Default.aspx`) |

> No hay propiedades configurables en `web.config` específicas para esta página. Todos los parámetros de presentación están hardcodeados en el markup y el code-behind.

---

## 8. Resumen

- **Componentes**: 1 backend (`Default.aspx.cs`), 2 frontend (`Default.aspx`, `Site.master` heredado), 3 entidades de datos (`Customers`, `Orders`, `Audit`)
- **Endpoints**: 1 (GET `/Default.aspx`)
- **Reglas de negocio**: 4 (cálculo de KPI: total clientes, clientes bloqueados, órdenes en REVIEW, suma de deuda pendiente)
- **Integraciones**: 3 internas (`LegacyRepository`, `Site.master`, `site.css`), 0 externas

---
**Nota**: Documento generado para el submódulo "Operations Dashboard Page" del módulo "Web User Interface (ASP.NET Web Forms)". Mantener documento < 1500 líneas. Priorizar información funcional esencial.
