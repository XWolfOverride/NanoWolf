# NanoWolf — ideas descartadas y aplazadas

Conceptos que se plantearon y no se adoptaron (o se dejaron para más adelante), con el motivo y
cuándo podrían volver.

## Descartadas

### Copiar NanoWolf dentro de cada aplicación

Como se hace con wolf.js: una copia de `src/` en cada proyecto, con un script de sincronización.
**Por qué no:** varias copias que divergen. Se prefiere una sola fuente referenciada como proyecto
y un registro de cambios de contrato que cada aplicación lee para adaptarse. **Podría volver:** si
una aplicación necesitara congelar una versión concreta sin depender del repositorio.

### Submódulo de git o paquete NuGet

**Por qué no:** el submódulo complica el flujo de trabajo y la compilación de un solo comando; el
paquete exige infraestructura de publicación y versionado. **Podría volver:** el paquete, si
NanoWolf lo usaran muchos proyectos o terceros.

### Mantener la compatibilidad con las aplicaciones existentes

**Por qué no:** frena la evolución del núcleo. Las aplicaciones se adaptan con
[cambios-de-contrato.md](cambios-de-contrato.md).

### Almacenamiento sin abstraer (MongoDB dentro del núcleo)

Era la decisión original en kNo. **Por qué no:** aplicaciones sin servidor de base de datos
(uso local). MongoDB queda en `NanoWolf.Mongo`.

## Aplazadas

### Compatibilidad con Native AOT

Generador de código fuente que produzca el catálogo y las llamadas al compilar, en lugar de
descubrirlos por reflexión (`GetTypes`, `Expression.Compile`, `Activator`). No hace falta en
servidores web. **Podría volver:** para un arranque más rápido o para usar NanoWolf en una
herramienta local que exija AOT.

### Cliente JavaScript común

El cliente de llamadas (lee el catálogo y crea una función por llamada) vive hoy en kNo
(`client/js/kno.js`). Pasaría aquí como `nanowolf.js` cuando lo necesite una segunda aplicación.

### Servidor MCP a partir del catálogo

Exponer las llamadas del catálogo como herramientas MCP para que agentes de IA externos usen una
aplicación igual que su interfaz. Surge del «director externo» de StoryTell.

### Almacenes en ficheros y en SQLite

Para aplicaciones de uso local o sin base de datos. Se harán cuando los necesite una aplicación
(StoryTell, fase 1: ajustes y registro de acciones en ficheros).
