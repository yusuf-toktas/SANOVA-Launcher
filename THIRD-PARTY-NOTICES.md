# Third-party notices

SANOVA Launcher's own source code is licensed under the [Apache License 2.0](LICENSE). The
launcher is built with, and its releases redistribute, the third-party components listed below.
Each is licensed by its authors under the license shown.

These notices concern software only. Neither the launcher's license nor the licenses below grant
any rights to *Grand Theft Auto: San Andreas* or to third-party game modifications. Mods have
their own licenses, recorded per component in each SANOVA content release.

Installed copies of the launcher include this file and the full license texts in the `licenses`
folder next to `SanovaLauncher.exe`.

## Components distributed with the launcher

| Component | Version | License | Copyright | Source |
|---|---|---|---|---|
| .NET runtime and libraries (self-contained), including Microsoft.Extensions.DependencyInjection, Logging, Options and Primitives | 10.0 | MIT | © .NET Foundation and Contributors; © Microsoft Corporation | [dotnet/runtime](https://github.com/dotnet/runtime) |
| Avalonia UI (Avalonia, Desktop, Win32, Skia, HarfBuzz, Themes.Fluent, Native, X11, FreeDesktop, FreeDesktop.AtSpi, Remote.Protocol) | 12.1.5 | MIT | © 2013-2026 The AvaloniaUI Project | [AvaloniaUI/Avalonia](https://github.com/AvaloniaUI/Avalonia) |
| ANGLE (Avalonia.Angle.Windows.Natives) | 2.1.27548 | BSD-3-Clause | © The ANGLE Project Authors | [ANGLE](https://chromium.googlesource.com/angle/angle) |
| SkiaSharp | 3.119.4 | MIT | © 2015-2016 Xamarin, Inc.; © 2017-2018 Microsoft Corporation | [mono/SkiaSharp](https://github.com/mono/SkiaSharp) |
| HarfBuzzSharp | 8.3.1.3 | MIT | © 2015-2016 Xamarin, Inc.; © 2017-2018 Microsoft Corporation | [mono/SkiaSharp](https://github.com/mono/SkiaSharp) |
| Native libraries in SkiaSharp and HarfBuzzSharp (Skia, HarfBuzz, FreeType, libpng, zlib and others) | — | Various permissive licenses | See the notices file below | — |
| MicroCom.Runtime | 0.11.6 | MIT | © 2021 Nikita Tsukanov | [kekekeks/MicroCom](https://github.com/kekekeks/MicroCom) |
| Tmds.DBus.Protocol | 0.94.1 | MIT | © Tom Deseyn | [tmds/Tmds.DBus](https://github.com/tmds/Tmds.DBus) |
| CommunityToolkit.Mvvm | 8.4.2 | MIT | © .NET Foundation and Contributors | [CommunityToolkit/dotnet](https://github.com/CommunityToolkit/dotnet) |
| Serilog, Serilog.Extensions.Logging, Serilog.Sinks.File | 4.4.0, 10.0.0, 7.0.0 | Apache-2.0 | © Serilog Contributors | [serilog](https://github.com/serilog) |
| Velopack | 1.2.161 | MIT | © Velopack Ltd. | [velopack/velopack](https://github.com/velopack/velopack) |
| Overpass typeface | — | SIL Open Font License 1.1 | © 2021 The Overpass Project Authors | [RedHatOfficial/Overpass](https://github.com/RedHatOfficial/Overpass) |
| Fluent UI System Icons (icon outlines embedded in `Themes/Icons.axaml`) | — | MIT | © 2020 Microsoft Corporation | [microsoft/fluentui-system-icons](https://github.com/microsoft/fluentui-system-icons) |

The launcher's artwork (hero scene, logo and application icon) is original and drawn in code.

## Development tools (not distributed)

Used only to build and test the launcher: xUnit (Apache-2.0), Microsoft.NET.Test.Sdk (MIT),
xunit.runner.visualstudio (Apache-2.0), Avalonia.Headless (MIT), the Velopack CLI `vpk` (MIT)
and Avalonia build services (MIT).

## License texts

### Apache License 2.0

Serilog uses the same license as SANOVA Launcher; the full text is in [LICENSE](LICENSE).

### MIT License

Applies to the components marked MIT above, each with its own copyright notice as listed.

```text
Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

### ANGLE (BSD 3-Clause License)

```text
Copyright 2018 The ANGLE Project Authors.
All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions
are met:

    Redistributions of source code must retain the above copyright
    notice, this list of conditions and the following disclaimer.

    Redistributions in binary form must reproduce the above
    copyright notice, this list of conditions and the following
    disclaimer in the documentation and/or other materials provided
    with the distribution.

    Neither the name of TransGaming Inc., Google Inc., 3DLabs Inc.
    Ltd., nor the names of their contributors may be used to endorse
    or promote products derived from this software without specific
    prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
"AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS
FOR A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE
COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT,
INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING,
BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT
LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN
ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE
POSSIBILITY OF SUCH DAMAGE.
```

### SIL Open Font License 1.1 (Overpass)

The full text is in [`src/Sanova.Launcher/Assets/Fonts/OFL.txt`](src/Sanova.Launcher/Assets/Fonts/OFL.txt),
installed as `licenses/Overpass-OFL.txt`.

### Notices bundled with the .NET runtime and with SkiaSharp/HarfBuzzSharp

The .NET runtime and the native SkiaSharp and HarfBuzzSharp libraries include further
third-party components. Their notices, copied unchanged from the corresponding NuGet packages,
are in [`src/Sanova.Launcher/licenses/`](src/Sanova.Launcher/licenses):

- `dotnet-runtime-THIRD-PARTY-NOTICES.txt` (Microsoft.NETCore.App.Runtime.win-x64 10.0.14)
- `SkiaSharp-HarfBuzzSharp-THIRD-PARTY-NOTICES.txt` (SkiaSharp.NativeAssets.Win32 3.119.4)

Refresh these files when upgrading .NET or SkiaSharp ([Releasing](docs/RELEASING.md)).
