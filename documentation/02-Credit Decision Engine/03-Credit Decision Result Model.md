# Especificación Funcional: Modelo de Resultado de Decisión de Crédito

**Módulo Padre**: Credit Decision Engine | **Fecha**: 2026-08-10

## 1. Resumen Ejecutivo

- **Propósito**: Objeto de transferencia de datos (DTO) simple que transporta el resultado completo de una evaluación crediticia desde el motor de decisión (`CreditDecisionService.Evaluate`) hasta su consumidor inmediato (`OrderEntry.aspx.cs`), y desde allí hacia la base de datos y la interfaz de usuario.
- **Funcionalidad Clave**:
  1. Encapsula los cinco campos de resultado de una decisión: estado, código, explicación, riesgo efectivo y exposición total tras el pedido.
  2. Es creado exclusivamente por `CreditDecisionService.Evaluate` y su método auxiliar `Fill`.
  3. Sus campos `Status`, `Code` y `Explanation` se persisten directamente en la tabla `Orders`.
  4. Sus campos `EffectiveRisk` y `ExposureAfterOrder` alimentan el registro de auditoría y la presentación visual al usuario.
  5. Es mutable sin restricciones — cualquier consumidor puede alterar sus valores tras la devolución del método.
- **Usuarios**: No tiene acceso de usuario directo; es un objeto interno del servidor consumido por el código-behind de `OrderEntry.aspx.cs` en respuesta a la acción de un operador de crédito.

---

## 2. Lógica de Negocio

### Procesos Clave

**Ciclo de vida del objeto de resultado**:
1. `CreditDecisionService.Evaluate` instancia un objeto `CreditDecision` vacío al inicio de la evaluación.
2. Los campos `EffectiveRisk` y `ExposureAfterOrder` se calculan y asignan antes de evaluar cualquier regla de decisión (líneas 46–47 de `CreditDecisionService.cs`).
3. La primera regla del pipeline que se cumple invoca `Fill`, que asigna `Status`, `Code` y `Explanation`, y devuelve el objeto al llamante.
4. `OrderEntry.aspx.cs:Submit_Click` recibe el objeto y lo usa para construir la fila de pedido, el registro de auditoría y el HTML de resultado.
5. El objeto no se almacena más allá del ciclo de solicitud HTTP; no se cachea ni se reutiliza.

**Uso en la persistencia del pedido**:
1. `order["Status"]` ← `decision.Status` (p. ej., `APPROVED`, `REVIEW`, `BLOCKED`)
2. `order["DecisionCode"]` ← `decision.Code` (p. ej., `A00`, `B01`, `R72`)
3. `order["DecisionText"]` ← `decision.Explanation` (texto legible por humanos)
4. La fila se añade a `ds.Tables["Orders"]` y se persiste con `LegacyRepository.Save(ds)`.

### Validaciones Críticas

| Campo | Validación | Comportamiento |
|-------|------------|----------------|
| `Status` | Uno de `APPROVED`, `REVIEW`, `BLOCKED` | Asignado por `Fill`; ninguna validación en el modelo mismo |
| `Code` | Código alfanumérico de 3 caracteres (p. ej., `A00`, `B01`) | Asignado por `Fill`; sin validación en el modelo |
| `EffectiveRisk` | Entero en el rango `[0, 100]` | El rango se garantiza en la lógica de puntuación, no en el modelo |
| `ExposureAfterOrder` | Decimal positivo | Sin restricción formal en el modelo; calculado como `deuda + pedidos_abiertos + importe` |
| `Explanation` | Cadena de texto libre | Sin restricción de longitud; persiste en `Orders.DecisionText` |

---

## 3. Componentes

### Backend

| Componente | Propósito | Responsabilidades Clave |
|-----------|-----------|------------------------|
| `CreditDecision` (clase, líneas 6–13 de `CreditDecisionService.cs`) | DTO de resultado de evaluación crediticia | Almacena `Status`, `Code`, `Explanation`, `EffectiveRisk`, `ExposureAfterOrder`; POCO público sin encapsulación |
| `CreditDecisionService.Fill` (líneas 85–89 de `CreditDecisionService.cs`) | Método auxiliar de construcción del resultado | Asigna los tres campos textuales (`Status`, `Code`, `Explanation`) y devuelve el objeto para permitir el patrón `return Fill(...)` |
| `CreditDecisionService.Evaluate` (línea 17 de `CreditDecisionService.cs`) | Productor del objeto de resultado | Crea la instancia, calcula los campos numéricos, invoca `Fill` con el resultado de la primera regla que se cumple, y devuelve el objeto completo |

### Frontend

| Componente | Propósito | Funcionalidad Principal |
|-----------|-----------|------------------------|
| `OrderEntry.aspx.cs:Submit_Click` (líneas 48–68) | Consumidor del objeto de resultado | Lee todos los campos para construir la fila del pedido, el registro de auditoría y el bloque HTML de resultado visible al operador |

### Datos

| Entidad | Propósito | Campos Principales Relacionados |
|---------|-----------|--------------------------------|
| `Orders` (tabla SQLite) | Persistencia del pedido con su decisión | `Status` (→ `decision.Status`), `DecisionCode` (→ `decision.Code`), `DecisionText` (→ `decision.Explanation`) |
| `Audit` (tabla SQLite) | Registro de auditoría de operaciones | Detalle compuesto: `"{Status}/{Code}; risk={EffectiveRisk}; exposure={ExposureAfterOrder}"` |

---

## 4. APIs y Endpoints

### Endpoints Principales

| Método | URL | Propósito | Auth |
|--------|-----|-----------|------|
| POST | `/OrderEntry.aspx` | Envío del formulario de pedido; ejecuta la evaluación y devuelve el resultado | No (sesión de aplicación ASP.NET) |

**Ejemplo de resultado renderizado**:
```html
<!-- Estado APPROVED -->
<div class='result ok'>
  <b>APPROVED / A00</b><br/>
  Within credit and risk limits<br/>
  Effective risk: 45<br/>
  Exposure after order: 1250.00 €
</div>

<!-- Estado BLOCKED -->
<div class='result bad'>
  <b>BLOCKED / B01</b><br/>
  Customer marked as blocked<br/>
  Effective risk: 78<br/>
  Exposure after order: 5000.00 €
</div>
```

**Registro de auditoría generado**:
```
Tipo: ORDER | Sujeto: ORD-20260810143022123
Detalle: "APPROVED/A00; risk=45; exposure=1250.00"
```

---

## 5. Flujos de Datos

### Flujo Principal: Creación y Consumo del Objeto de Resultado

```
Operador → [Envía formulario OrderEntry] → Submit_Click
    → LegacyRepository.Load (DataSet)
    → Validación importe mínimo (< 25 EUR + INVOICE → rechazo previo)
    → CreditDecisionService.Evaluate(ds, customer, amount, paymentMethod, priority)
        → [Cálculo de riesgo efectivo] → d.EffectiveRisk = risk
        → [Cálculo de exposición]      → d.ExposureAfterOrder = debt + openOrders + amount
        → [Pipeline de reglas] → Fill(d, status, code, text) → return d
    → decision.Status / decision.Code / decision.Explanation → Orders row
    → decision.EffectiveRisk / decision.ExposureAfterOrder  → Audit detail
    → LegacyRepository.Save(ds)
    → HTML renderizado al navegador
```

**Pasos resumidos**:
1. El formulario web envía los parámetros del pedido (cliente, importe, método de pago, prioridad).
2. `Evaluate` construye el objeto `CreditDecision` y rellena los campos numéricos antes de evaluar reglas.
3. La primera regla del pipeline que se cumple llama a `Fill` y devuelve el objeto con los campos textuales completos.
4. `Submit_Click` persiste la decisión en `Orders` y escribe el detalle en `Audit`.
5. El HTML de resultado se genera con los cinco campos del objeto y se muestra al operador.

### Manejo de Errores

| Error | Causa | Acción |
|-------|-------|--------|
| Importe inválido | `Amount.Text` no parseable o ≤ 0 | Devuelve HTML `result bad` sin llegar a crear el objeto `CreditDecision` |
| Cliente no encontrado | `CustomerId` no existe en el `DataSet` | Devuelve HTML `result bad` sin crear el objeto |
| Pedido < 25 EUR con INVOICE | Validación en `Submit_Click` línea 42 | Devuelve HTML `result warn`; el motor nunca ve este caso |
| Excepción en `Evaluate` | Error de BD o dato nulo inesperado | No capturado; ASP.NET muestra error 500 estándar |

---

## 6. Integraciones

### Internas

- **Decision Rule Pipeline** (`CreditDecisionService.Evaluate`, líneas 54–82): Productor del objeto; asigna `Status`, `Code`, `Explanation` mediante `Fill` en el primer punto de salida que se cumpla.
- **Risk Score Computation** (`CreditDecisionService.Evaluate`, líneas 33–43): Escribe `EffectiveRisk` en el objeto antes de que el pipeline de reglas comience.
- **Data Access and Persistence Layer** (`LegacyRepository.Save`, `AddAudit`): Consumidor final; almacena los campos del objeto en `Orders` y `Audit`.
- **Web User Interface** (`OrderEntry.aspx.cs:Submit_Click`, líneas 56–67): Consumidor directo; lee todos los campos para persistencia y renderizado.

### Externas

- Ninguna. El objeto de resultado no cruza límites de sistema externo.

---

## 7. Configuración

| Propiedad | Propósito | Default |
|-----------|-----------|---------|
| N/A | El modelo `CreditDecision` en sí no consume configuración | — |

> Los umbrales que determinan qué valores se asignan al objeto son responsabilidad del **Decision Rule Pipeline** y el **Configuration Parameter Resolver** (`HighRiskScore`, `ManualReviewAmount`, `HardBlockOverdueDays`, `LegacyCountrySurcharge`).

---

## 8. Resumen

- **Componentes**: 2 backend (clase `CreditDecision` + método `Fill`), 1 frontend (`Submit_Click` como consumidor), 2 entidades de datos (`Orders`, `Audit`)
- **Endpoints**: 1 (POST `/OrderEntry.aspx`)
- **Reglas de negocio**: 0 propias — el modelo es un DTO pasivo; las reglas residen en el pipeline de decisión
- **Integraciones**: 4 internas, 0 externas

### Notas de Riesgo de Migración

- El objeto es un POCO público mutable sin encapsulación. Cualquier código que reciba la referencia puede alterar los valores tras la devolución de `Evaluate`, lo que puede causar inconsistencias si en la arquitectura destino se añaden capas intermedias.
- Los campos `Status`, `Code` y `Explanation` no tienen enumerados ni constantes definidas; los valores posibles solo se conocen leyendo el pipeline de reglas.
- La división de responsabilidades entre el motor (`CreditDecisionService`) y el código-behind (`OrderEntry.aspx.cs`) hace que la validación del mínimo de 25 EUR sea invisible para el objeto de resultado: nunca se crea un `CreditDecision` con ese tipo de rechazo, lo que puede generar lagunas en los registros de auditoría durante la migración.

---
**Nota**: Documento generado a partir del análisis directo del código fuente. Se ha priorizado la información funcional esencial y los riesgos de migración identificados.
