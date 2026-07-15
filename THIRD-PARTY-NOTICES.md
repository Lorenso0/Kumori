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

## ppy/osu and osu.Framework

Kumori and Kumori Replay Viewer use the official ppy/osu, osu.Framework, and
osu! ruleset NuGet packages under the MIT License.

Copyright (c) 2025 ppy Pty Ltd <contact@ppy.sh>.

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
THE SOFTWARE.

## Other managed dependencies

The published application also contains third-party components distributed by
their respective projects, including CommunityToolkit.Mvvm (MIT),
Microsoft.Data.Sqlite and SQLite (MIT/public domain), Realm .NET (Apache-2.0),
Serilog (Apache-2.0), and Microsoft.Toolkit.Uwp.Notifications (MIT). Their
package metadata and source repositories contain the authoritative copyright
and licence texts. This notice must remain with redistributed copies of Kumori.

Kumori's build tooling uses Mono.Cecil under the MIT License to update osu!'s
legacy AutoMapper constructor calls in copied build outputs. The NuGet package
cache and upstream package remain unchanged.
