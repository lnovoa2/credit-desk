# Especificación Funcional: Manejador de Cierre de Aplicación

**Módulo Padre**: Application Bootstrap and Global Lifecycle | **Fecha**: 2026-08-10

## 1. Resumen Ejecutivo

- **Propósito**: Gestionar el cierre limpio de los recursos en segundo plano cuando el dominio de aplicación ASP.NET es descargado, evitando hilos huérfanos y fugas de recursos.
- **Funcionalidad Clave**:
  - Captura el evento `Application_End` del ciclo de vida ASP.NET
  - Invoca `NightlyBatch.Stop()` para liberar el temporizador de fondo (`System.Threading.Timer`)
  - Pone a `null` la referencia interna al temporizador para evitar uso posterior
  - Garantiza que no queden procesos de batch activos tras el apagado del contenedor o reciclado de IIS
- **Usuarios**: No tiene usuarios directos — es invocado exclusivamente por el motor de ejecución ASP.NET. Los administradores de sistema son los beneficiarios indirectos al obtener un apagado predecible y sin errores.

---

## 2. Lógica de Negocio

### Procesos Clave

**Cierre del temporizador de batch nocturno**: Detiene de forma controlada el temporizador periódico que ejecuta el proceso de recálculo de riesgo crediticio.

1. El motor ASP.NET detecta el apagado del dominio (reciclo de IIS, parada explícita o detención del contenedor Docker).
2. El runtime dispara el evento `Application_End` en `Global.asax` (líneas 9–12).
3. Se invoca `NightlyBatch.Stop()`.
4. `Stop()` comprueba si `_timer` no es `null` y llama a `_timer.Dispose()`.
5. La referencia `_timer` se establece a `null`, liberando el objeto de forma definitiva.

### Validaciones Críticas

| Campo | Validación | Resultado |
|-------|------------|-----------|
| `_timer` | Comprobación de nulidad antes de `Dispose()` | Sin excepción si el temporizador nunca se inició |

> **Nota**: No existe manejo de excepciones en `Application_End`. Un fallo en `Stop()` propagará una excepción no capturada durante el cierre del dominio, aunque en la práctica el impacto es limitado ya que el dominio ya está siendo destruido.

---

## 3. Componentes

### Backend

| Componente | Propósito | Responsabilidades Clave |
|-----------|-----------|------------------------|
| `Global.asax` — `Application_End` | Punto de entrada del cierre de aplicación | Captura el evento del runtime y delega en `NightlyBatch.Stop()` |
| `NightlyBatch.Stop()` (`App_Code/NightlyBatch.cs`, línea 20–24) | Liberación del temporizador | Llama a `Timer.Dispose()` y pone la referencia a `null` |

### Frontend

_No aplica — este subcomponente no tiene interfaz de usuario._

### Datos

_No aplica — el cierre no interactúa con la base de datos ni con estado persistente._

---

## 4. APIs y Endpoints

_No aplica — este subcomponente no expone ni consume endpoints HTTP. Es activado exclusivamente por el runtime ASP.NET mediante el evento del ciclo de vida de la aplicación._

---

## 5. Flujos de Datos

### Flujo Principal: Cierre del dominio ASP.NET

```
[Runtime ASP.NET]
      |
      v  Application_End (Global.asax:9)
[Application_End handler]
      |
      v
[NightlyBatch.Stop() (NightlyBatch.cs:20)]
      |
      +-- _timer != null ?
      |         |
      |        Sí → _timer.Dispose()  →  _timer = null
      |         |
      |        No → no-op
      v
[Temporizador liberado / recursos de SO devueltos]
```

**Pasos resumidos**:
1. El runtime ASP.NET desencadena `Application_End` al descargar el dominio.
2. El handler invoca `NightlyBatch.Stop()`.
3. `Stop()` libera el `System.Threading.Timer` si estaba activo.
4. La referencia interna queda a `null`; no pueden dispararse más ticks.
5. El dominio se descarga sin hilos ni temporizadores pendientes.

### Manejo de Errores

| Error | Causa | Acción |
|-------|-------|--------|
| Excepción en `Stop()` | Error inesperado al llamar a `Dispose()` | Propagación sin capturar; el dominio se descarga igualmente (sin fallback implementado) |
| `_timer` ya es `null` | `Stop()` llamado sin que `Start()` haya completado | No-op seguro por la comprobación de nulidad |

---

## 6. Integraciones

### Internas

- **Nightly Batch Processing — `NightlyBatch.Stop()`**: Único consumidor de este método. Recibe la orden de parar el temporizador y libera el objeto `System.Threading.Timer`. No se intercambian datos; la integración es de control de ciclo de vida.
- **Application Startup Handler** (`Global.asax`, `Application_Start`): Subcomponente simétrico que inició el temporizador. El cierre deshace exactamente lo que el inicio creó.

### Externas

_No aplica — no existen integraciones con sistemas externos en este subcomponente._

---

## 7. Configuración

| Propiedad | Propósito | Default |
|-----------|-----------|---------|
| `BatchIntervalSeconds` (`web.config`) | Controla el intervalo del temporizador que este subcomponente para al cierre | `45` segundos (mínimo forzado: `10`) |

> `BatchIntervalSeconds` no es leída directamente por el manejador de cierre, pero define el comportamiento del temporizador que se está liberando. Valores bajos incrementan la frecuencia con la que el batch podría estar en ejecución en el momento del cierre.

---

## 8. Resumen

- **Componentes**: 2 backend, 0 frontend, 0 datos
- **Endpoints**: 0
- **Reglas de negocio**: 1 (comprobación de nulidad antes de `Dispose`)
- **Integraciones**: 2 internas, 0 externas

---

**Nota**: Documento mantenido bajo 1500 líneas. Se prioriza información funcional esencial sobre detalles de implementación de bajo nivel.
