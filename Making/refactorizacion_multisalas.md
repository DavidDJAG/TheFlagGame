# Pedido de refactorización: soporte de múltiples salas en TheFlagGame

## Estado

Documento de requerimiento técnico.

Este documento describe la refactorización solicitada para el servidor de **TheFlagGame**.

## Contexto del proyecto

**TheFlagGame** es un juego 2D multijugador tipo *capture the flag* con:

- cliente web/PWA en HTML, CSS y JavaScript;
- servidor autoritativo en C# con ASP.NET Core;
- comunicación en tiempo real mediante WebSockets;
- mapa cargado desde JSON;
- editor web de mapas;
- simulación autoritativa de jugadores, disparos, banderas, puntajes, temporizador y reinicio de partida.

Actualmente el servidor debe considerarse diseñado alrededor de una única partida global en memoria. Todos los jugadores conectados al endpoint WebSocket participan en la misma partida, reciben el mismo snapshot y comparten el mismo estado runtime.

## Objetivo general

Refactorizar el servidor para permitir **múltiples salas de juego independientes**, donde los jugadores sean sincronizados únicamente con otros jugadores de la sala a la que se hayan unido.

Cada sala debe comportarse como una partida independiente, con su propio estado de juego, pero reutilizando la lógica existente de simulación autoritativa.

## Alcance principal

La refactorización solicitada aplica primero al **servidor**. No es necesario mantener retrocompatibilidad interna de código, ya que se trata de un desarrollo nuevo sobre la base existente.

Sí debe mantenerse una experiencia funcional mínima para el cliente web actual: si el cliente se conecta sin indicar sala, debe ingresar a una sala por defecto.

## Requerimientos funcionales

### 1. Salas independientes

El servidor debe permitir múltiples salas activas en simultáneo.

Cada sala debe tener estado independiente de:

- jugadores conectados;
- clientes WebSocket;
- equipos;
- posiciones;
- inputs;
- puntajes;
- banderas;
- portadores de bandera;
- disparos;
- efectos de impacto;
- temporizador de partida;
- estado de partida finalizada;
- ganador, perdedor o empate;
- reinicio de partida.

Un jugador conectado a una sala no debe aparecer, colisionar, disparar, recibir disparos ni compartir eventos con jugadores de otra sala.

### 2. Unión a sala mediante WebSocket

El endpoint WebSocket debe aceptar un identificador de sala.

Formato recomendado:

```text
/ws?room=<roomId>
```

Ejemplos:

```text
/ws?room=public
/ws?room=alpha
/ws?room=test-01
```

El servidor debe normalizar y validar el `roomId`.

Si el cliente no envía `room`, el servidor debe asignarlo automáticamente a una sala por defecto:

```text
public
```

Esto permite que el cliente web actual funcione sin cambios iniciales.

### 3. Sala por defecto

El endpoint:

```text
/ws
```

debe ser equivalente a:

```text
/ws?room=public
```

La sala `public` debe crearse automáticamente cuando sea necesaria.

### 4. Creación automática de salas

Si un jugador intenta conectarse a una sala válida que no existe y todavía hay capacidad para crear nuevas salas, el servidor debe crearla automáticamente.

Ejemplo:

```text
/ws?room=alpha
```

Si `alpha` no existe, debe crearse en el momento de conexión.

### 5. Aislamiento del estado mutable del mapa

El mapa base puede seguir siendo global, cargado desde:

```text
server/Data/map.json
```

Sin embargo, cada sala debe tener su propio estado runtime de banderas.

No debe compartirse entre salas ningún objeto mutable que represente estado de partida, especialmente:

- posición actual de bandera;
- `CarriedByPlayerId`;
- estado de bandera en base o caída.

Esto es crítico porque las banderas derivan del mapa, pero su estado cambia durante la partida.

### 6. Límite de jugadores por sala

El límite actual de jugadores debe aplicarse **por sala**, no de forma global.

Valor inicial requerido:

```text
MaxPlayersPerRoom = 32
```

Ejemplo esperado:

```text
public  -> hasta 32 jugadores
alpha   -> hasta 32 jugadores
test-01 -> hasta 32 jugadores
```

Si una sala alcanza el límite, nuevas conexiones a esa sala deben rechazarse con una respuesta adecuada, por ejemplo `429 Too Many Requests` o cierre WebSocket con motivo claro.

### 7. Límite de salas activas

Agregar un límite configurable o constante para cantidad máxima de salas activas.

Valor sugerido inicial:

```text
MaxActiveRooms = 24
```

Comportamiento requerido:

- Si la sala solicitada ya existe, debe permitirse la conexión mientras la sala tenga cupo.
- Si la sala solicitada no existe y se alcanzó `MaxActiveRooms`, el servidor debe rechazar la creación de la nueva sala.
- El rechazo debe ser explícito y registrarse en logs.

Respuesta esperada sugerida:

```text
The maximum number of active rooms has been reached.
```

### 8. Limpieza de salas vacías

El servidor debe poder eliminar salas que queden vacías.

Requerimiento mínimo:

- Cuando el último jugador sale de una sala, la sala puede eliminarse inmediatamente o marcarse para eliminación.
- Debe evitarse eliminar la sala mientras todavía existan clientes registrados.
- La sala `public` puede recrearse automáticamente cuando vuelva a entrar un cliente.

Recomendación:

- Para evitar thrashing, se puede usar una política de retención corta, por ejemplo limpiar salas vacías después de 3 minutos.
- Para una primera versión, se acepta limpieza inmediata si el diseño es simple y seguro.

### 9. Límite global opcional de jugadores

Además del límite por sala y del límite de salas activas, se puede contemplar un límite global de jugadores.

Recomendación: Actualmente **no implementar**.

### 10. Mensaje de bienvenida con sala

El mensaje inicial `welcome` enviado por el servidor debe incluir el identificador de sala.

Ejemplo:

```json
{
  "type": "welcome",
  "roomId": "alpha",
  "playerId": "p-...",
  "team": "blue",
  "tickRate": 20,
  "mapName": "Blaze Field"
}
```

### 11. Snapshots por sala

El servidor debe construir y enviar snapshots únicamente a los clientes de la misma sala.

El payload `state` debe contener solo:

- jugadores de la sala;
- banderas de la sala;
- disparos de la sala;
- eventos de la sala;
- puntajes de la sala;
- estado de partida de la sala.

No debe haber mezcla de snapshots entre salas.

### 12. Reset por sala

El mensaje actual:

```json
{ "type": "resetGame" }
```

debe reiniciar únicamente la sala del jugador que lo envía.

No debe afectar otras salas.

### 13. Ping/pong por sala

El comportamiento actual de `ping` y `pong` debe mantenerse.

La respuesta `pong` solo debe enviarse al cliente correspondiente.

### 14. Desconexión por sala

Cuando un cliente se desconecta:

- debe eliminarse solo de su sala;
- si llevaba una bandera, la bandera afectada debe resolverse solo dentro de esa sala;
- no debe modificar estado de otras salas.

### 15. Reemplazo de mapa

El editor de mapas puede seguir operando contra endpoints globales:

```text
GET /api/map
PUT /api/map
```

No se requiere modificar el editor de mapas para la primera versión.

Regla requerida:

- `GET /api/map` devuelve el mapa base global.
- `PUT /api/map` reemplaza el mapa global solo si no hay jugadores conectados en ninguna sala activa.

Si hay jugadores conectados en cualquier sala, `PUT /api/map` debe rechazar el cambio con `409 Conflict`.

Motivo:

- Evitar que salas activas queden con estado de mapa inconsistente.
- Evitar mezclar mapas base nuevos con estados runtime ya inicializados.

## Requerimientos de arquitectura

### 1. Separar administrador de salas y partida

Crear una capa de administración de salas.

Diseño sugerido:

```text
GameRoomManager
  ├── Room "public"  -> GameRoom
  ├── Room "alpha"   -> GameRoom
  └── Room "test-01" -> GameRoom
```

### 2. `GameRoomManager`

Responsabilidades:

- normalizar `roomId`;
- validar nombres de sala;
- crear salas bajo demanda;
- aplicar `MaxActiveRooms`;
- aplicar límite global de jugadores si se implementa;
- enrutar conexiones WebSocket hacia la sala correspondiente;
- exponer métricas globales;
- listar salas activas;
- eliminar salas vacías;
- coordinar reemplazo del mapa global;
- iniciar y detener las salas al iniciar o detener el servidor.

### 3. `GameRoom`

Responsabilidades:

- contener una partida independiente;
- mantener jugadores y clientes de esa sala;
- ejecutar simulación autoritativa;
- procesar inputs;
- procesar disparos;
- resolver colisiones;
- resolver banderas;
- manejar puntajes;
- manejar temporizador;
- manejar reset;
- construir snapshots;
- emitir eventos;
- limpiar clientes desconectados;
- detener recursos al cerrar la sala.

La lógica actualmente concentrada en `GameHost` debe moverse o adaptarse a `GameRoom`.

### 4. Carga y clonación de mapa

Crear una utilidad o servicio para cargar y validar el mapa.

Diseño sugerido:

```text
MapLoader
```

Responsabilidades:

- leer `map.json`;
- validar estructura;
- construir representación runtime;
- entregar una nueva instancia segura para cada sala;
- evitar compartir `FlagRuntime` mutable entre salas.

### 5. Modelos

Actualizar modelos relacionados con conexión y salas.

Sugerencias:

```csharp
public sealed class ConnectedClient
{
    public required string PlayerId { get; init; }
    public required string RoomId { get; init; }
    ...
}
```

Agregar modelos auxiliares para métricas:

```csharp
public sealed record RoomSummary(
    string RoomId,
    int PlayerCount,
    int MaxPlayers,
    string MapName,
    string MatchStatus
);
```

Agregar resumen global:

```csharp
public sealed record RoomManagerSummary(
    int ActiveRooms,
    int MaxActiveRooms,
    int TotalPlayers,
    int MaxPlayersPerRoom
);
```

## Endpoints requeridos

### `GET /health`

Debe reflejar estado global del servidor con salas.

Respuesta sugerida:

```json
{
  "status": "ok",
  "activeRooms": 2,
  "maxActiveRooms": 64,
  "players": 5,
  "maxPlayersPerRoom": 32,
  "tickRate": 20,
  "map": "map.json"
}
```

### `GET /api/map`

Sin cambios funcionales para el editor.

Debe devolver el mapa base global.

### `PUT /api/map`

Debe reemplazar el mapa base global solo si no hay jugadores conectados en ninguna sala.

Si hay jugadores conectados:

```http
409 Conflict
```

Respuesta sugerida:

```json
{
  "ok": false,
  "message": "The map cannot be replaced while players are connected in active rooms. Disconnect everyone and try again."
}
```

### `GET /api/rooms`

Nuevo endpoint recomendado.

Debe listar salas activas.

Respuesta sugerida:

```json
{
  "activeRooms": 2,
  "maxActiveRooms": 64,
  "totalPlayers": 5,
  "rooms": [
    {
      "roomId": "public",
      "playerCount": 3,
      "maxPlayers": 32,
      "mapName": "Blaze Field",
      "matchStatus": "running"
    },
    {
      "roomId": "alpha",
      "playerCount": 2,
      "maxPlayers": 32,
      "mapName": "Blaze Field",
      "matchStatus": "running"
    }
  ]
}
```

### `POST /api/rooms`

Nuevo endpoint recomendado.

Permite crear una sala explícitamente.

Solicitud sugerida:

```json
{
  "roomId": "alpha"
}
```

Respuesta exitosa sugerida:

```json
{
  "ok": true,
  "roomId": "alpha"
}
```

Si se alcanzó el límite:

```http
429 Too Many Requests
```

Respuesta sugerida:

```json
{
  "ok": false,
  "message": "The maximum number of active rooms has been reached."
}
```

### `WS /ws?room=<roomId>`

Endpoint WebSocket principal.

Si `room` está vacío o ausente, usar `public`.

## Reglas de validación de `roomId`

El identificador de sala debe normalizarse.

Reglas sugeridas:

- trim de espacios;
- convertir a minúsculas;
- valor por defecto `public` si está vacío;
- longitud mínima: 1;
- longitud máxima sugerida: 32 o 48 caracteres;
- caracteres permitidos: letras, números, guion y guion bajo.

Regex sugerida:

```regex
^[a-z0-9_-]{1,32}$
```

Nombres inválidos deben rechazarse con error claro.

## Compatibilidad con cliente actual

El cliente actual debe seguir funcionando sin cambios iniciales porque se conectará a:

```text
/ws
```

y el servidor lo enviará a:

```text
public
```

Para probar múltiples salas antes de modificar la UI del cliente, se puede cambiar temporalmente la URL WebSocket en el cliente:

```js
const socket = new WebSocket(`${WS_URL}?room=alpha`);
```

Más adelante, el cliente debería incorporar:

- input de sala;
- botón para crear sala;
- botón para unirse a sala;
- visualización de sala actual;
- enlace compartible con código de sala;
- opcionalmente listado de salas públicas.

## Impacto en el editor de mapas

No se solicita modificar el editor en esta etapa.

El editor continúa trabajando contra el mapa global.

Flujo esperado:

1. El editor carga el mapa con `GET /api/map`.
2. El usuario modifica el mapa.
3. El editor guarda con `PUT /api/map`.
4. El servidor acepta el cambio solo si no hay jugadores conectados en ninguna sala.

Futuras mejoras fuera del alcance inicial:

- mapas por sala;
- selección de mapa al crear sala;
- editor asociado a una sala;
- banco de mapas persistentes;
- votación de mapa por sala.

## Seguridad y abuso

La refactorización debe mantener los controles actuales:

- validación de `Origin` para WebSocket;
- CORS restringido;
- límite de tamaño de mensajes entrantes;
- rate limit por cliente;
- timeout de clientes inactivos;
- colas outbound por cliente;
- cierre de clientes lentos o con socket fallido.

Además, debe agregarse protección contra abuso de salas:

- `MaxActiveRooms`;
- validación estricta de `roomId`;
- rechazo explícito de nuevas salas si se alcanza el límite;
- logs de rechazos;
- limpieza de salas vacías.

## Logging requerido

Agregar logs para:

- creación de sala;
- eliminación de sala;
- conexión de cliente indicando sala;
- desconexión de cliente indicando sala;
- rechazo por sala llena;
- rechazo por límite de salas activas;
- rechazo por `roomId` inválido;
- reset de partida indicando sala;
- finalización de partida indicando sala;
- reemplazo de mapa global;
- rechazo de reemplazo de mapa por jugadores conectados.

Ejemplos:

```text
Room created: alpha
Client connected p-... in room alpha (blue)
Room alpha is full
Maximum active rooms reached
Match reset requested by p-... in room alpha
Room removed because it is empty: alpha
```

## Documentación requerida

Actualizar `server/README.md` para documentar:

- nuevo modelo de salas;
- sala por defecto `public`;
- uso de `/ws?room=<roomId>`;
- límite por sala;
- límite de salas activas;
- endpoints nuevos;
- comportamiento del editor de mapas;
- reglas de reemplazo de mapa;
- ejemplos de prueba local;
- limitaciones actuales.

## Criterios de aceptación

La refactorización se considera correcta si cumple lo siguiente:

1. Conectar dos clientes a `/ws?room=alpha` hace que ambos se vean entre sí.
2. Conectar un cliente a `/ws?room=beta` no muestra jugadores de `alpha`.
3. Un disparo en `alpha` no afecta jugadores de `beta`.
4. Un reset en `alpha` no reinicia `beta`.
5. El score de `alpha` es independiente del score de `beta`.
6. Las banderas de `alpha` son independientes de las banderas de `beta`.
7. `/ws` sin parámetro conecta a `public`.
8. El cliente actual puede funcionar usando la sala `public`.
9. El límite de 32 jugadores se aplica por sala.
10. Al alcanzar `MaxActiveRooms`, no se crean más salas nuevas.
11. `GET /api/rooms` lista salas activas y cantidad de jugadores.
12. `PUT /api/map` se rechaza si hay jugadores conectados en cualquier sala.
13. El editor de mapas sigue pudiendo usar `GET /api/map` y `PUT /api/map`.
14. Las salas vacías se limpian o quedan controladas sin pérdida de recursos.
15. Los logs permiten auditar creación, conexión, desconexión, rechazo y eliminación de salas.
