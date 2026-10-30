# LegacyCreditDesk

Fixture legacy compacto para probar herramientas de migración de aplicaciones.

## Objetivo

Simula un monolito empresarial ASP.NET Web Forms de estilo 2005-2010 para gestión de pedidos a crédito. Está diseñado para ser pequeño y barato de procesar repetidamente, pero no trivial de migrar: la lógica funcional está repartida entre UI/code-behind, servicio de negocio, acceso ADO.NET clásico, SQL, estado global y un proceso background.

## Stack legacy

- ASP.NET Web Forms (`.aspx` + code-behind).
- C# con estilo antiguo, sin LINQ, DI, async/await ni ORM.
- `DataSet`, `DataTable`, `DataRow`, `Hashtable` y estado estático.
- ADO.NET clásico con `Mono.Data.Sqlite` / SQLite.
- SQLite 3 **embebida**: no existe servidor de base de datos externo.
- SQL embebido, transacciones, índices, vistas y trigger.
- `System.Threading.Timer` iniciado desde `Global.asax` para el proceso batch.
- Configuración histórica mediante `web.config`.

## Base de datos embebida

La aplicación crea automáticamente al primer acceso:

```text
App_Data/legacy-credit-desk.db
```

No hay que instalar ni configurar SQL Server, Oracle, PostgreSQL, MySQL ni otro contenedor.

La BBDD contiene:

- `Customers`
- `Orders`
- `Audit`
- `vw_CustomerExposure`
- `vw_ManualReviewQueue`
- índices para pedidos
- trigger `trg_OrderBlockedAudit`
- datos seed de clientes, pedidos y auditoría

Parte del acceso usa `DataSet` en memoria y parte vuelve directamente a SQLite mediante SQL parametrizado. Esta inconsistencia es intencionada: obliga a una herramienta de migración a reconstruir correctamente las dependencias de datos y el comportamiento, no solo a traducir clases.

## Arranque local recomendado

Solo necesitas Docker:

```bash
docker compose up --build
```

Abrir en el navegador:

```text
http://localhost:8080
```

Es **un solo servicio**: web, batch y BBDD están en el mismo monolito/contenedor. El volumen Docker únicamente conserva el fichero SQLite entre reinicios.

## Verificación automática

```bash
./scripts/verify-docker.sh
```

El smoke test:

1. construye la imagen;
2. levanta la aplicación;
3. solicita `Default.aspx` por HTTP;
4. comprueba el contenido de la respuesta;
5. verifica que `/app/App_Data/legacy-credit-desk.db` haya sido creado y no esté vacío.

## Reset completo de la BBDD

```bash
./scripts/reset-data.sh
```

Esto elimina el volumen de datos y vuelve a arrancar. En el siguiente acceso se recrean esquema + seed automáticamente.

También puede hacerse manualmente:

```bash
docker compose down -v
docker compose up --build
```

## Arranque sin Docker

Si la máquina ya tiene Mono/XSP4 y `Mono.Data.Sqlite`:

```bash
./scripts/run-local.sh
```

Después abrir `http://localhost:8080`.

## Dominio y dificultad funcional

Un pedido puede terminar como `APPROVED`, `REVIEW` o `BLOCKED`. La decisión depende, entre otros factores, de:

- límite de crédito;
- deuda actual;
- exposición por pedidos anteriores;
- antigüedad del cliente;
- país;
- días vencidos;
- score de riesgo;
- medio de pago;
- prioridad;
- importe;
- recurrencia de pedidos;
- bloqueo comercial;
- override manual.

Hay reglas cuyo orden importa, excepciones históricas y reglas parcialmente duplicadas. El batch recalcula riesgo y puede bloquear automáticamente clientes, pero no los desbloquea.

La documentación de `docs/business-rules.md` está deliberadamente incompleta: **el código y el SQL son la fuente real de comportamiento**.

## Escenarios de prueba

1. `C001`, 250 EUR, `TRANSFER`: caso favorable.
2. `C002`, 2500 EUR, `INVOICE`: exposición/deuda fuerzan revisión o bloqueo según estado.
3. `C003`: cliente de alto riesgo/bloqueado.
4. Pedido >= 7500 EUR: revisión salvo excepciones codificadas.
5. Activar `ManualOverride` y repetir el pedido: cambia el camino de decisión.
6. Ejecutar `/Batch.aspx`: recalcula riesgos y bloqueos.
7. Generar un pedido `BLOCKED`: interviene también el trigger SQLite de auditoría.

## Estructura relevante

```text
LegacyCreditDesk/
├── App_Code/
│   ├── CreditDecisionService.cs   # núcleo de reglas
│   ├── LegacyRepository.cs        # ADO.NET + schema SQLite + SQL + persistencia
│   ├── LegacyState.cs             # estado global
│   └── NightlyBatch.cs            # proceso background
├── App_Data/                      # legacy-credit-desk.db se genera aquí
├── Default.aspx                   # dashboard
├── Customers.aspx                 # clientes / override
├── OrderEntry.aspx                # alta y decisión de pedidos
├── Batch.aspx                     # estado y ejecución manual del batch
├── Global.asax                    # bootstrap del batch
├── web.config
├── Dockerfile
├── docker-compose.yml
├── scripts/
│   ├── run-local.sh
│   ├── reset-data.sh
│   └── verify-docker.sh
└── docs/
```

## Por qué SQLite

Para este fixture interesa separar la dificultad de **migrar una aplicación legacy** de la dificultad de montar su infraestructura. SQLite mantiene relaciones, SQL, transacciones, índices, vistas y triggers, pero la BBDD es un único fichero local. Esto permite destruir y recrear el escenario continuamente con muy poco coste.

## Compatibilidad

La aplicación utiliza ASP.NET 4.x sobre Mono/XSP para seguir siendo ejecutable hoy en Linux/macOS mediante Docker, pero su diseño y APIs reproducen deliberadamente patrones de aplicaciones Web Forms antiguas.
