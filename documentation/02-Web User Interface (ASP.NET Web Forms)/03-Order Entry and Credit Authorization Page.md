# Especificación Funcional: Página de Entrada de Pedidos y Autorización de Crédito

**Módulo Padre**: Web User Interface (ASP.NET Web Forms) | **Fecha**: 2026-08-10

## 1. Resumen Ejecutivo

- **Propósito**: Formulario web que permite registrar un nuevo pedido para un cliente, evaluar su riesgo crediticio mediante el motor de decisiones y persistir el resultado junto con su trazabilidad de auditoría.
- **Funcionalidad Clave**:
  - Captura de parámetros del pedido: cliente, importe, método de pago y prioridad
  - Validación de importe (numérico positivo, mínimo 25 EUR para INVOICE)
  - Invocación del motor de decisión crediticia (`CreditDecisionService`)
  - Persistencia del pedido y registro de auditoría en la base de datos SQLite
  - Presentación visual del resultado (APPROVED / REVIEW / BLOCKED) con código y explicación
  - Visualización de los 10 últimos pedidos registrados
- **Usuarios**: Operadores de crédito con acceso a la aplicación web interna

---

## 2. Lógica de Negocio

### Procesos Clave

**Validación previa al motor de decisión**:
1. El importe introducido se normaliza (coma → punto) y se parsea con `InvariantCulture`
2. Si no es numérico o es ≤ 0, se rechaza con mensaje de error (no se llama al motor)
3. Si el importe es < 25 EUR **y** el método de pago es INVOICE, se rechaza con advertencia
4. Se carga el cliente desde el `DataSet`; si no existe, error inmediato

**Evaluación de riesgo crediticio** (`CreditDecisionService.Evaluate`):
1. Se calcula el riesgo efectivo aplicando ajustes secuenciales sobre `RiskScore` del cliente
2. Se calcula la exposición total: `OutstandingDebt + OpenOrders + Amount`
3. Se evalúan reglas de bloqueo (B01, B90, B35), revisión (R72, R11, R75, R20) y aprobación (A15, A00, AOV) en orden fijo con cortocircuito
4. Se devuelve un objeto `CreditDecision` con `Status`, `Code`, `Explanation`, `EffectiveRisk`, `ExposureAfterOrder`

**Persistencia y presentación**:
1. Se crea una nueva fila en `Orders` con el ID generado, los datos del pedido y la decisión
2. Se escribe un registro en `Audit` con tipo `ORDER`, el ID del pedido como `Subject` y el resumen de la decisión
3. Se llama a `LegacyRepository.Save()` para persistir en SQLite dentro de una transacción
4. Se renderiza el panel de resultado con código CSS (`ok` / `warn` / `bad`) según el estado

### Validaciones Críticas

| Campo | Validación | Error mostrado |
|-------|------------|----------------|
| Amount | Numérico positivo, parseo InvariantCulture | "Invalid amount." |
| Amount + Payment | Importe < 25 EUR con método INVOICE | "Orders below 25 EUR cannot be invoiced." |
| CustomerId | Cliente encontrado en el DataSet | "Customer not found." |
| Blocked (cliente) | `Blocked=true` sin `ManualOverride` | BLOCKED / B01 |
| OverdueDays | ≥ umbral `HardBlockOverdueDays` (default 90) | BLOCKED / B90 |
| Exposure | > 135% del límite de crédito sin override | BLOCKED / B35 |

### Ajustes de Riesgo (motor de decisión)

| Condición | Ajuste sobre RiskScore |
|-----------|------------------------|
| País con recargo (`LegacyCountrySurcharge`) | +7 |
| ≥ 3 pedidos en los últimos 14 días | +8 |
| Deuda > 65% del límite de crédito | +12 |
| Cliente con antigüedad < 730 días (2 años) | +10 |
| Método de pago CARD | −3 |
| Método de pago INVOICE | +4 |
| Prioridad URGENT | +5 |
| Importe > 3 000 EUR | +5 |
| Importe > 10 000 EUR | +12 adicionales |
| RiskScore resultante acotado a | [0, 100] |

### Reglas de Decisión (por orden de evaluación)

| Código | Estado | Condición |
|--------|--------|-----------|
| B01 | BLOCKED | `Blocked=true` y sin `ManualOverride` |
| B90 | BLOCKED | `OverdueDays` ≥ `HardBlockOverdueDays` (default 90) — no salvable con override |
| B35 | BLOCKED | Exposición > 135% del límite sin `ManualOverride` |
| R72 | REVIEW | Riesgo efectivo ≥ `HighRiskScore` (default 72) sin override |
| R11 | REVIEW | Exposición > límite + método INVOICE sin override |
| R75 | REVIEW | Importe ≥ `ManualReviewAmount` (default 7 500 EUR) y método ≠ CARD |
| R20 | REVIEW | Prioridad URGENT con `OverdueDays` > 20 |
| A15 | APPROVED | Antigüedad ≥ 15 años, exposición ≤ 110% del límite y riesgo < 65 |
| A00 | APPROVED | Exposición ≤ límite y riesgo < `HighRiskScore` |
| AOV | APPROVED | `ManualOverride=true` y exposición ≤ 150% del límite |
| R99 | REVIEW | Ninguna regla anterior produjo aprobación automática |

> **Nota crítica de migración**: La validación INVOICE < 25 EUR reside en `OrderEntry.aspx.cs`, no en `CreditDecisionService`. Cualquier migración que porte únicamente el servicio perderá silenciosamente esta regla de negocio.

---

## 3. Componentes

### Backend

| Componente | Propósito | Responsabilidades Clave |
|-----------|-----------|------------------------|
| `OrderEntry.aspx.cs` (`OrderEntryPage`) | Code-behind de la página de pedidos | Validación de importe, guard 25 EUR/INVOICE, orquestación del flujo, generación del ID de pedido, persistencia y renderizado del resultado |
| `CreditDecisionService` | Motor de evaluación crediticia | Ajuste de riesgo, cálculo de exposición, evaluación de reglas en orden fijo con cortocircuito |
| `LegacyRepository` | Capa de acceso a datos | `Load()`, `FindCustomer()`, `AddAudit()`, `Save()`, `SumOpenOrders()`, `CountRecentOrders()` |

### Frontend

| Componente | Propósito | Funcionalidad Principal |
|-----------|-----------|------------------------|
| `OrderEntry.aspx` | Formulario de entrada de pedidos | Dropdown de clientes, campo importe, dropdown método de pago, dropdown prioridad, botón de envío |
| `Result` (Literal) | Panel de resultado de decisión | Muestra estado (APPROVED/REVIEW/BLOCKED), código, explicación, riesgo efectivo y exposición resultante con color CSS |
| `RecentOrdersGrid` (GridView) | Tabla de últimos pedidos | Muestra los 10 pedidos más recientes en orden inverso de creación |

### Datos

| Entidad | Propósito | Campos Principales |
|---------|-----------|-------------------|
| `Customers` | Perfiles de crédito de clientes | `Id`, `Name`, `CreditLimit`, `OutstandingDebt`, `OverdueDays`, `RiskScore`, `Blocked`, `ManualOverride`, `Since`, `Country` |
| `Orders` | Registro de pedidos y decisiones | `Id`, `CustomerId`, `Amount`, `PaymentMethod`, `Priority`, `Status`, `DecisionCode`, `DecisionText`, `CreatedAt` |
| `Audit` | Trazabilidad de operaciones | `Id`, `At`, `Type`, `Subject`, `Detail` |

---

## 4. APIs y Endpoints

Este submódulo no expone APIs REST. La interacción es exclusivamente mediante postback HTTP del formulario ASP.NET Web Forms.

### Endpoints Principales

| Método | URL | Propósito | Auth |
|--------|-----|-----------|------|
| GET | `/OrderEntry.aspx` | Carga inicial: rellena dropdown de clientes y muestra últimos pedidos | No (aplicación interna) |
| POST | `/OrderEntry.aspx` | Envío del formulario: valida, evalúa, persiste y devuelve resultado | No (aplicación interna) |

**Parámetros del formulario (POST)**:
```
CustomerId   → ID del cliente seleccionado (e.g. "C001")
Amount       → Importe como texto (e.g. "1500" o "1500,00")
Payment      → INVOICE | TRANSFER | CARD
Priority     → NORMAL | URGENT
```

**Respuesta (HTML embebida en el Literal `Result`)**:
```html
<!-- APPROVED -->
<div class='result ok'><b>APPROVED / A00</b><br/>Within credit and risk limits<br/>Effective risk: 45<br/>Exposure after order: 2450.00 €</div>

<!-- REVIEW -->
<div class='result warn'><b>REVIEW / R72</b><br/>Effective risk is above review threshold<br/>...</div>

<!-- BLOCKED -->
<div class='result bad'><b>BLOCKED / B01</b><br/>Customer marked as blocked<br/>...</div>
```

---

## 5. Flujos de Datos

### Flujo Principal: Envío de Pedido

```
Usuario → [Rellena formulario] → POST /OrderEntry.aspx
    → Validación de importe (parse + positivo)
    → Guard 25 EUR/INVOICE
    → LegacyRepository.Load() → DataSet
    → LegacyRepository.FindCustomer() → DataRow cliente
    → CreditDecisionService.Evaluate()
        ├── LegacyRepository.SumOpenOrders() [consulta SQL directa]
        ├── LegacyRepository.CountRecentOrders() [consulta SQL directa]
        └── Evaluación secuencial de reglas → CreditDecision
    → Creación de fila Orders en DataSet
    → LegacyRepository.AddAudit() → fila Audit en DataSet
    → LegacyRepository.Save() → transacción SQLite
    → Renderizado del panel Result (HTML con clase CSS)
    → BindRecentOrders() → LegacyRepository.Load() → RecentOrdersGrid
```

**Pasos resumidos**:
1. Validación de entrada en code-behind (importe, cliente, guard INVOICE)
2. Evaluación crediticia mediante motor externo (`CreditDecisionService`)
3. Construcción del registro de pedido y registro de auditoría
4. Persistencia transaccional en SQLite
5. Renderizado del resultado y refresco de la tabla de pedidos recientes

### Manejo de Errores

| Error | Causa | Acción |
|-------|-------|--------|
| Importe inválido | No numérico o ≤ 0 | Panel `bad`: "Invalid amount." — sin persistencia |
| Importe < 25 EUR + INVOICE | Guard comercial en code-behind | Panel `warn`: "Orders below 25 EUR cannot be invoiced." — sin persistencia |
| Cliente no encontrado | `FindCustomer()` devuelve null | Panel `bad`: "Customer not found." — sin persistencia |
| BLOCKED (B01/B90/B35) | Reglas de bloqueo del motor | Pedido persistido con `Status='BLOCKED'`; trigger BD escribe registro adicional en `Audit` |
| REVIEW (Rxx) | Reglas de revisión del motor | Pedido persistido con `Status='REVIEW'`; queda en cola de revisión manual |

---

## 6. Integraciones

### Internas

- **Credit Decision Engine** (`CreditDecisionService`): Invocado en cada envío de formulario — recibe `DataSet`, `DataRow` del cliente, importe, método de pago y prioridad; devuelve `CreditDecision` con estado, código, explicación, riesgo efectivo y exposición
- **Data Access and Persistence Layer** (`LegacyRepository`): Fuente única de datos y escritura — carga el `DataSet` completo, resuelve el cliente, calcula exposición y pedidos recientes con SQL directo, persiste transaccionalmente
- **Master Page Layout and Navigation** (`Site.master`): Herencia del layout compartido, navegación y hoja de estilos; los nombres de clase CSS `ok`, `warn`, `bad` son hardcoded en el code-behind

### Externas

- Ninguna. La aplicación no consume servicios externos ni realiza llamadas HTTP de salida.

---

## 7. Configuración

| Propiedad (`web.config`) | Propósito | Default |
|--------------------------|-----------|---------|
| `HardBlockOverdueDays` | Umbral de días vencidos para bloqueo automático incondicional (B90) | 90 |
| `HighRiskScore` | Puntuación de riesgo a partir de la cual se requiere revisión (R72) | 72 |
| `ManualReviewAmount` | Importe mínimo para revisión manual en pedidos no-CARD (R75) | 7500 |
| `LegacyCountrySurcharge` | Lista de países (separados por coma) con penalización de riesgo +7 | *(vacío)* |

---

## 8. Resumen

- **Componentes**: 3 backend (`OrderEntryPage`, `CreditDecisionService`, `LegacyRepository`), 3 frontend (`OrderEntry.aspx`, `Result` Literal, `RecentOrdersGrid`), 3 entidades de datos (`Customers`, `Orders`, `Audit`)
- **Endpoints**: 2 (GET carga inicial, POST evaluación y persistencia)
- **Reglas de negocio**: 11 reglas de decisión en motor + 1 validación INVOICE fuera del motor (25 EUR mínimo)
- **Integraciones**: 3 internas (motor de decisión, repositorio, master page), 0 externas

---

**Nota**: Documento mantenido < 1500 líneas. Prioridad en lógica funcional esencial.
> **Riesgo de migración destacado**: La regla de importe mínimo 25 EUR para pedidos por INVOICE está implementada en `OrderEntry.aspx.cs` (línea 42), no en `CreditDecisionService.cs`. Esta regla no aparece en `docs/business-rules.md`. Cualquier migración que extraiga únicamente el motor de decisión ignorará esta validación comercial.
