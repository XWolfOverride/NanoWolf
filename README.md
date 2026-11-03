# NanoWolf

Base reutilizable para aplicaciones web pequeñas en ASP.NET Core (.NET 10): publica métodos C#
como llamadas con catálogo (el cliente las usa como funciones), con contexto explícito, errores
uniformes, registro de acciones, usuarios con invitación, métodos de acceso activables,
administración, ajustes y preferencias.

Es una biblioteca: las aplicaciones la referencian como proyecto. Ver
[DOC/arquitectura.md](DOC/arquitectura.md).

## Proyectos

- `src/NanoWolf`: el núcleo, sin dependencias de base de datos.
- `src/NanoWolf.Mongo`: almacenes en MongoDB.
- `src/NanoWolf.Testing` y `src/NanoWolf.Mongo.Testing`: utilidades para las pruebas de las
  aplicaciones.

## Compatibilidad

Los cambios que rompen la compatibilidad se anotan con fecha en
[DOC/cambios-de-contrato.md](DOC/cambios-de-contrato.md).

## Pruebas

```
dotnet test
```

## Licencia

MIT. Ver [LICENSE](LICENSE).
