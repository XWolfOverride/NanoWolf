# NanoWolf — arquitectura

Base reutilizable para aplicaciones web pequeñas sobre ASP.NET Core: publicación de métodos C#
como llamadas con catálogo, contexto explícito, errores uniformes, registro de acciones,
usuarios con invitación y métodos de acceso activables, administración, ajustes y preferencias.
Sin plugins, módulos ni servicios: una forma uniforme de publicar métodos y el armazón común.

Nació dentro de kNo; el diseño original y su razonamiento están en la documentación de ese
proyecto (`DOC/02-llamadas.md`, `DOC/03-autenticacion.md`). Este documento resume lo necesario
para usarlo.

## Proyectos

| Proyecto | Ensamblado | Contenido | Dependencias |
| --- | --- | --- | --- |
| `src/NanoWolf` | `XWolf.NanoWolf` | Llamadas (`[Call]`, `[CallGroup]`, `[Singleton]`), catálogo, `Ctx`, errores, registro, respuestas especiales, proxies de confianza, autenticación, usuarios, invitaciones, administración, ajustes, preferencias, interfaces de almacenes | ASP.NET Core |
| `src/NanoWolf.Mongo` | `XWolf.NanoWolf.Mongo` | Almacenes en MongoDB, índices, convenciones de mapeo | núcleo, `MongoDB.Driver` |
| `src/NanoWolf.Testing` | `XWolf.NanoWolf.Testing` | Almacenes en memoria, `AuthApp` (servidor de pruebas con autenticación), `Browser`, `ListLog`, pruebas de contrato de almacenes | núcleo, TestHost, xUnit |
| `src/NanoWolf.Mongo.Testing` | `XWolf.NanoWolf.Mongo.Testing` | `MongoTestRun` (pruebas contra MongoDB real, se omiten sin variables de entorno) | Mongo, xUnit |
| `tests/NanoWolf.Tests` | — | Pruebas de NanoWolf | todo lo anterior |

## Uso en una aplicación

Referencia como proyecto. Por defecto el repositorio está junto al de la aplicación
(`xwolf-git/NanoWolf`); la aplicación lo localiza con una propiedad de MSBuild, por ejemplo en su
`Directory.Build.props`:

```xml
<NanoWolfDir Condition="'$(NanoWolfDir)' == '' and '$(NANOWOLF_DIR)' != ''">$(NANOWOLF_DIR)</NanoWolfDir>
<NanoWolfDir Condition="'$(NanoWolfDir)' == ''">$(MSBuildThisFileDirectory)..\..\NanoWolf</NanoWolfDir>
```

y `<ProjectReference Include="$(NanoWolfDir)/src/NanoWolf/NanoWolf.csproj" />`. En integración
continua y en imágenes de contenedor se descarga aparte y se indica con `NANOWOLF_DIR` (ver kNo:
`Jenkinsfile`, `deploy/Dockerfile.api`).

Arranque típico:

```csharp
builder.Services.AddNanoWolfMongo(o => …);          // o almacenes propios (IUserStore, ISessionStore…)
builder.Services.AddNanoWolfAuth(o => …);           // usuarios, sesiones, ajustes, preferencias
builder.Services.AddNanoWolfCalls([typeof(Program).Assembly], o => …);
var app = builder.Build();
app.UseNanoWolfBase();                              // proxies de confianza, cabeceras
app.MapNanoWolfCalls();                             // /rpc/catalog y /rpc/{grupo}/{metodo}
```

Una aplicación sin cuentas (por ejemplo, de uso local) registra su propio `IUserResolver` (quién
es el usuario de cada petición) e `IActionLog` (dónde se registran las acciones) en lugar de
`AddNanoWolfAuth`.

## Puntos de extensión

- `IUserResolver`, `IActionLog`: identidad y registro.
- `IUserStore`, `ISessionStore`, `ITokenStore`, `IThrottleStore`, `ISettingsStore`: almacenes.
  Toda implementación nueva debe pasar `StoreContractTests` (de `NanoWolf.Testing`).
- `ICallResponse`: respuestas que escriben su propia respuesta HTTP (descargas…).
- `AddNanoWolfSetting`, `AddNanoWolfPreference`: ajustes globales y preferencias por usuario.
- `ISettingsRule`: reglas que validan un cambio de ajustes antes de aplicarlo.

## Convenciones

- Identificadores en inglés (código, llamadas, JSON). Los textos de interfaz son de cada
  aplicación.
- Comentarios del código en inglés; documentación en `DOC/` en español.
- No usa Native AOT (descubrimiento por reflexión): pensado para servidores web.

## Compatibilidad

NanoWolf evoluciona sin mantener las aplicaciones que lo usan. Todo cambio que rompa la
compatibilidad se anota con fecha en [cambios-de-contrato.md](cambios-de-contrato.md).

## Pruebas

```
dotnet test
```

Las pruebas contra MongoDB real necesitan `NANOWOLF_MONGO` (cadena de conexión) y
`NANOWOLF_MONGO_DB` (una base cuyo nombre contenga `dev` o `test`); sin ellas se omiten. Crean
colecciones `nwtest_…` y solo borran esas.
