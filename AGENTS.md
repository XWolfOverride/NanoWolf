# NanoWolf — contexto para agentes de IA

Biblioteca base para aplicaciones web pequeñas en ASP.NET Core: llamadas con catálogo, `Ctx`,
errores, registro, usuarios, administración, ajustes. Este fichero es un índice; el detalle está
en `DOC/`.

| Documento | Contenido |
| --- | --- |
| [DOC/arquitectura.md](DOC/arquitectura.md) | Proyectos, uso desde una aplicación, puntos de extensión, convenciones, pruebas |
| [DOC/cambios-de-contrato.md](DOC/cambios-de-contrato.md) | Cambios que rompen la compatibilidad, con fecha y cómo adaptarse |
| [DOC/descartes.md](DOC/descartes.md) | Ideas descartadas y aplazadas |

## Estado actual (2026-11-03)

- Extraído de kNo. MongoDB separado del núcleo (`NanoWolf.Mongo`); utilidades de prueba en
  `NanoWolf.Testing` / `NanoWolf.Mongo.Testing`. 206 pruebas (186 siempre; 20 contra MongoDB real
  con variables de entorno).
- Lo usan kNo (referencia de proyecto) y, en diseño, StoryTell.
- **Git y GitHub los gestiona el responsable del proyecto: no hacer commits, `push` ni crear
  repositorios.**
- **Documentación impersonal:** sin nombres, direcciones IP ni datos de una máquina concreta.

## Reglas

- **Todo cambio que rompa la compatibilidad** (API pública, protocolo, datos guardados,
  utilidades de prueba) se anota con fecha en `DOC/cambios-de-contrato.md`: qué, por qué y cómo
  adaptarse. NanoWolf no mantiene las aplicaciones que lo usan; ellas leen ese registro.
- Lo descartado o aplazado se anota en `DOC/descartes.md`.
- Identificadores y comentarios de código en inglés; documentación en español.
- Sin Native AOT (reflexión): es para servidores web.

## Compilar

`dotnet build`, `dotnet test` (solución `NanoWolf.slnx`).
