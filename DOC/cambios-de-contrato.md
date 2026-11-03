# NanoWolf — cambios de contrato

Registro de los cambios que **rompen la compatibilidad** con las aplicaciones que usan NanoWolf:
API pública (tipos, métodos, opciones), comportamiento observable de las llamadas o del protocolo
HTTP, formato de los datos guardados y utilidades de prueba.

NanoWolf **no mantiene** las aplicaciones que lo usan: cuando cambia algo de esta lista, cada
aplicación se adapta leyendo este documento. Por eso cada entrada dice qué cambia, por qué y qué
tiene que hacer una aplicación para adaptarse.

Formato: una sección por fecha (la más reciente primero); dentro, una entrada por cambio.

## 2026-11-03 — Extracción a repositorio propio

NanoWolf deja de vivir dentro de kNo y pasa a este repositorio. Las aplicaciones lo referencian
como proyecto (por defecto, en una carpeta hermana: `../NanoWolf`).

### MongoDB sale del núcleo

- **Qué cambia:** `AddNanoWolfMongo`, `EnsureNanoWolfIndexesAsync`, `MongoContext`,
  `MongoOptions`, `MongoMapping` y los almacenes de MongoDB pasan al ensamblado
  `XWolf.NanoWolf.Mongo` (proyecto `src/NanoWolf.Mongo`). El espacio de nombres no cambia
  (`XWolf.NanoWolf`). El núcleo (`XWolf.NanoWolf`) ya no depende de `MongoDB.Driver`.
- **Por qué:** aplicaciones sin MongoDB (almacenes en ficheros u otros).
- **Adaptación:** quien use MongoDB referencia también `src/NanoWolf.Mongo/NanoWolf.Mongo.csproj`.
  Sin cambios de código.

### Utilidades de prueba en `NanoWolf.Testing`

- **Qué cambia:** los almacenes en memoria (`MemoryUserStore`, `MemorySessionStore`,
  `MemoryTokenStore`, `MemoryThrottleStore`, `MemorySettingsStore`), `AuthApp`, `Browser`,
  `ListLog`, `StoreSet` y `StoreContractTests` pasan del proyecto de pruebas de NanoWolf a la
  biblioteca `src/NanoWolf.Testing` (ensamblado `XWolf.NanoWolf.Testing`), con espacio de nombres
  `XWolf.NanoWolf.Testing` (antes `XWolf.NanoWolf.Tests`). `MongoTestRun` pasa a
  `src/NanoWolf.Mongo.Testing` (mismo espacio de nombres `XWolf.NanoWolf.Testing`).
- **Por qué:** las pruebas de las aplicaciones las usan; no deben depender del proyecto de pruebas
  de NanoWolf.
- **Adaptación:** referenciar `NanoWolf.Testing` (y `NanoWolf.Mongo.Testing` si se prueba contra
  MongoDB) en lugar de `NanoWolf.Tests`; cambiar `using XWolf.NanoWolf.Tests;` por
  `using XWolf.NanoWolf.Testing;`.

### `AuthApp.CreateAsync` sin llamadas de ejemplo por defecto

- **Qué cambia:** si no se pasa `calls`, `AuthApp` publica solo las llamadas de NanoWolf (`auth`,
  `admin`, `settings`, `account`). Antes añadía `ItemCalls`, una clase de ejemplo de las pruebas de
  NanoWolf.
- **Adaptación:** pasar las llamadas propias en `calls` (lo normal ya era hacerlo).
