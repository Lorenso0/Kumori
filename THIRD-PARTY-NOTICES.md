# Third-Party Notices

## tosu

Kumori uses the upstream vanilla build of tosu, distributed as `tosu.exe`,
to provide osu! state over a local WebSocket.

- Source/project: https://github.com/tosuapp/tosu
- Release asset: https://github.com/tosuapp/tosu/releases
- License: GNU Lesser General Public License v3.0 only
- Copyright: 2023-2026 Mikhail Babynichev

The managed `tosu.exe` is stored under the user's Kumori application data
folder and may be replaced or removed by the user. Kumori configures vanilla
tosu to listen on `127.0.0.1:24051` via `tosu.env`.
