# Especificación Funcional: Cálculo de Puntuación de Riesgo

**Módulo Padre**: Credit Decision Engine | **Fecha**: 2026-08-10

---

## 1. Resumen Ejecutivo

- **Propósito**: Calcular la puntuación de riesgo efectiva de un cliente antes de que se evalúe cualquier regla de decisión crediticia. Parte del valor base almacenado en base de datos y aplica una serie de ajustes aditivos y sustractivos en función de atributos del cliente, del pedido y del contexto operativo.
- **Funcionalidad Clave**:
  1. Aplica 9 ajustes secuenciales sobre la puntuación base del cliente (`RiskScore`).
  2. Incorpora datos en tiempo real (pedidos abiertos, frecuencia reciente) consultando directamente SQLite.
  3. Aplica recargo por país mediante lista configurable en `web.config`.
  4. Limita el resultado al intervalo `[0, 100]`.
  5. Escribe el valor efectivo (`EffectiveRisk`) en el objeto de resultado `CreditDecision`, que sirve de entrada para el pipeline de reglas de decisión.
- **Usuarios**: No tiene interfaz de usuario directa; es invocado internamente por `CreditDecisionService.Evaluate` en cada solicitud de evaluación de pedido.

---

## 2. Lógica de Negocio

### Procesos Clave

**Cálculo de Puntuación de Riesgo Efectiva**: Transforma la puntuación base del cliente en una puntuación ajustada que refleja el riesgo real del pedido en curso.

1. Leer la puntuación base del cliente (`RiskScore`) desde el `DataRow` en memoria.
2. Consultar SQLite para obtener la suma de pedidos abiertos (`SumOpenOrders`) y el número de pedidos recientes en los últimos 14 días (`CountRecentOrders`).
3. Aplicar los 9 ajustes secuenciales (ver tabla de ajustes).
4. Acotar el resultado: si `risk < 0` → `risk = 0`; si `risk > 100` → `risk = 100`.
5. Asignar el valor resultante a `CreditDecision.EffectiveRisk` y continuar con el pipeline de decisión.

### Ajustes de Puntuación (orden de aplicación)

| # | Condición | Ajuste | Justificación de Negocio |
|---|-----------|--------|--------------------------|
| 1 | País del cliente en lista `LegacyCountrySurcharge` | `+7` | Recargo histórico por riesgo país (por defecto: PT, IT, GR) |
| 2 | ≥ 3 pedidos en los últimos 14 días | `+8` | Alta frecuencia de pedidos recientes indica mayor exposición operativa |
| 3 | `OutstandingDebt > CreditLimit × 0.65` | `+12` | Deuda superior al 65 % del límite señala presión de liquidez |
| 4 | Antigüedad del cliente < 730 días (< 2 años) | `+10` | Clientes nuevos con historial insuficiente |
| 5 | Método de pago = `CARD` | `−3` | Pago con tarjeta reduce el riesgo de impago |
| 6 | Método de pago = `INVOICE` | `+4` | Factura aplaza el cobro, aumentando la exposición |
| 7 | Prioridad = `URGENT` | `+5` | Pedidos urgentes implican menor tiempo de verificación |
| 8 | Importe del pedido > 3 000 € | `+5` | Umbral de importe elevado |
| 9 | Importe del pedido > 10 000 € | `+12` (acumulado sobre ajuste 8) | Umbral de importe muy elevado (se suma al ajuste anterior) |

> **Nota crítica de migración**: Los ajustes 8 y 9 son acumulativos. Un pedido superior a 10 000 € recibe `+5 + +12 = +17` en total. Esto no es explícito en ninguna documentación externa y debe replicarse exactamente en el sistema destino.

### Validaciones Críticas

| Campo | Validación | Efecto |
|-------|------------|--------|
| `risk` (resultado) | `< 0` → acotar a `0` | Evita puntuaciones negativas |
| `risk` (resultado) | `> 100` → acotar a `100` | Limita el rango al máximo absoluto |
| `LegacyCountrySurcharge` | Comparación sin distinción entre mayúsculas/minúsculas | Previene fallos de configuración por variaciones de capitalización |

---

## 3. Componentes

### Backend

| Componente | Propósito | Responsabilidades Clave |
|-----------|-----------|------------------------|
| `CreditDecisionService.Evaluate` (líneas 20–43) | Contiene la lógica de cálculo de riesgo | Leer atributos del cliente, ejecutar los 9 ajustes, acotar el resultado, asignar `EffectiveRisk` |
| `CreditDecisionService.IsSurchargeCountry` (líneas 91–98) | Verificar si el país tiene recargo | Leer `LegacyCountrySurcharge` de `web.config`, comparación sin distinción entre mayúsculas/minúsculas |
| `LegacyRepository.SumOpenOrders` (líneas 86–96) | Suma de pedidos abiertos del cliente | Consulta directa a SQLite; excluye pedidos con estado `BLOCKED` |
| `LegacyRepository.CountRecentOrders` (líneas 98–110) | Contador de pedidos recientes | Consulta directa a SQLite; ventana configurable de días (fija en 14 días en este contexto) |

### Frontend

No aplica. Este submódulo no tiene componentes de interfaz de usuario.

### Datos

| Entidad | Propósito | Campos Principales Usados |
|---------|-----------|--------------------------|
| `Customers` | Datos base del cliente | `RiskScore`, `OutstandingDebt`, `CreditLimit`, `OverdueDays`, `Country`, `Since` |
| `Orders` | Pedidos del cliente para cálculo de exposición | `CustomerId`, `Amount`, `Status`, `CreatedAt` |

---

## 4. APIs y Endpoints

Este submódulo no expone endpoints HTTP propios. Es una función interna de `CreditDecisionService`.

### Firma de la Función

```csharp
// Entrada (parámetros relevantes para este submódulo)
public static CreditDecision Evaluate(
    DataSet ds,          // Dataset en memoria (Customers, Orders, Audit)
    DataRow customer,    // Fila del cliente a evaluar
    decimal amount,      // Importe del pedido
    string paymentMethod,// "CARD" | "INVOICE" | "TRANSFER"
    string priority      // "NORMAL" | "URGENT"
)

// Salida (campos escritos por este submódulo)
CreditDecision.EffectiveRisk       // int [0-100]: puntuación ajustada final
CreditDecision.ExposureAfterOrder  // decimal: deuda + pedidos abiertos + importe del pedido
```

### Consultas SQL Internas

```sql
-- SumOpenOrders: suma de pedidos no bloqueados del cliente
SELECT COALESCE(SUM(Amount), 0)
FROM Orders
WHERE CustomerId = @id AND Status <> 'BLOCKED'

-- CountRecentOrders: pedidos en los últimos N días
SELECT COUNT(*)
FROM Orders
WHERE CustomerId = @id AND CreatedAt >= @edge
```

---

## 5. Flujos de Datos

### Flujo Principal: Cálculo de Puntuación de Riesgo

```
OrderEntry.aspx.cs (Submit_Click)
    → CreditDecisionService.Evaluate(ds, customer, amount, paymentMethod, priority)
        → LegacyRepository.SumOpenOrders(ds, customerId)    [consulta SQLite]
        → LegacyRepository.CountRecentOrders(ds, customerId, 14) [consulta SQLite]
        → IsSurchargeCountry(country)                       [lee web.config]
        → [Aplicar ajustes 1-9 secuencialmente]
        → [Acotar risk a [0, 100]]
        → d.EffectiveRisk = risk
        → d.ExposureAfterOrder = debt + openOrders + amount
    → [Continúa con Configuration Parameter Resolver y Decision Rule Pipeline]
```

**Pasos resumidos**:
1. Leer valores del cliente desde el `DataRow` en memoria.
2. Obtener métricas de pedidos directamente de SQLite (patrón de deuda técnica: el `DataSet` ya está en memoria pero se ignora para estas consultas).
3. Aplicar los 9 ajustes en orden estricto; el orden es semánticamente significativo.
4. Acotar y publicar el resultado en `CreditDecision`.

### Manejo de Errores

| Error | Causa | Acción |
|-------|-------|--------|
| `LegacyCountrySurcharge` ausente o vacío en `web.config` | Clave no configurada | `IsSurchargeCountry` devuelve `false` para todos los países; no se aplica recargo |
| Excepción de conversión de tipo en atributos del cliente | Datos corruptos en BD | Excepción no controlada; se propaga hasta `Submit_Click` y produce error HTTP 500 |
| Timeout de SQLite | Carga elevada o bloqueo | `PRAGMA busy_timeout=5000` (5 segundos); pasado este tiempo se lanza excepción |

---

## 6. Integraciones

### Internas

- **Data Access and Persistence Layer** (`LegacyRepository`): Proporciona `SumOpenOrders` y `CountRecentOrders` mediante consultas SQLite directas. Esto introduce un patrón de acceso mixto (DataSet en memoria + SQL directo) que es deuda técnica deliberada.
- **Configuration Parameter Resolver** (`IsSurchargeCountry`): Compartido entre este submódulo y el pipeline de decisión; lee `web.config` en cada llamada sin caché.
- **Decision Rule Pipeline**: Consume directamente el valor `EffectiveRisk` calculado aquí dentro del mismo ámbito del método `Evaluate`.
- **Credit Decision Result Model** (`CreditDecision`): Recibe los campos `EffectiveRisk` y `ExposureAfterOrder` escritos por este submódulo.

### Externas

No aplica. Este submódulo no tiene integraciones con sistemas externos.

---

## 7. Configuración

| Propiedad | Propósito | Valor por Defecto |
|-----------|-----------|-------------------|
| `LegacyCountrySurcharge` | Lista de códigos de país (separados por comas) que reciben el recargo de `+7` puntos de riesgo | `PT,IT,GR` |

> Los umbrales `HighRiskScore`, `ManualReviewAmount` y `HardBlockOverdueDays` son leídos en la fase posterior del pipeline de decisión, no en este submódulo.

---

## 8. Resumen

- **Componentes**: 4 backend (lógica de ajuste en `Evaluate`, `IsSurchargeCountry`, `SumOpenOrders`, `CountRecentOrders`), 0 frontend, 2 entidades de datos (`Customers`, `Orders`)
- **Endpoints**: 0 (función interna)
- **Reglas de negocio**: 9 ajustes secuenciales + 2 reglas de acotación
- **Integraciones**: 4 internas (LegacyRepository, ConfigurationResolver, DecisionRulePipeline, CreditDecision), 0 externas

---

### Notas de Migración Críticas

1. **Orden estricto de ajustes**: Los 9 ajustes deben aplicarse en el orden exacto del código. Cambiar el orden altera el resultado para clientes que activen varias condiciones simultáneamente.
2. **Acumulación de umbrales de importe**: Pedidos > 10 000 € reciben `+17` en total (`+5` por superar 3 000 € más `+12` por superar 10 000 €).
3. **Acceso mixto a datos**: `SumOpenOrders` y `CountRecentOrders` consultan SQLite directamente aunque el `DataSet` ya está en memoria. El sistema de destino debe decidir si mantiene este patrón o unifica el acceso.
4. **`IsSurchargeCountry` sin caché**: La lista de países se lee y parsea desde `web.config` en cada invocación de `Evaluate`. Bajo carga alta, esto puede ser un punto de contención.
5. **Asimetría de `manualOverride`**: El flag `manualOverride` NO interviene en el cálculo de la puntuación de riesgo; solo tiene efecto en las reglas de decisión posteriores.

---
**Nota**: Documento < 1500 líneas. Prioridad: lógica funcional esencial y reglas de negocio críticas para migración.
