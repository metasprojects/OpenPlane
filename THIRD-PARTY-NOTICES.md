# Third-party notices

OpenPlane is MIT-licensed (see `LICENSE`). It uses or installs the following projects, each under its own licence.
The release's `licenses/` folder holds the licence text of everything it ships (`licenses/README.txt` says which file
covers what).

| Project | Used for | Licence | Shipped with OpenPlane? |
|---|---|---|---|
| [Blueprinter](https://github.com/nikkorap/NOBlueprinter-Releases) by nikkorap | Loader the OpenPlane mod builds on | MIT | Yes, unmodified (`mod/Blueprinter`), with its licence |
| [BepInEx 5](https://github.com/BepInEx/BepInEx) | Mod loader for Nuclear Option | LGPL-2.1 | No; the app downloads the official release when you install the mod |
| [Harmony](https://github.com/pardeike/Harmony) | Runtime patching (comes with BepInEx) | MIT | No (part of BepInEx) |
| [Newtonsoft.Json](https://github.com/JamesNK/Newtonsoft.Json) by James Newton-King | JSON | MIT | Yes: the app ships `Newtonsoft.Json.dll`. The mod uses the copy that ships with Nuclear Option |
| [three.js](https://github.com/mrdoob/three.js) | 3D view in the app | MIT | Yes (`wwwroot/vendor/three`) |
| [Photino.NET](https://github.com/tryphotino/photino.NET) and [Photino.Native](https://github.com/tryphotino/photino.Native) by TryPhotino | The app's window | Apache-2.0 | Yes (`Photino.NET.dll`, `Photino.Native.dll`) |
| Microsoft Edge WebView2 loader (`WebView2Loader.dll`, from the WebView2 SDK, shipped inside Photino.Native) | Starts the WebView2 runtime that shows the app's page | Microsoft WebView2 SDK licence (BSD-style) | Yes |
| [.NET](https://github.com/dotnet/runtime) and [ASP.NET Core](https://github.com/dotnet/aspnetcore) runtimes | The app is self-contained, so players don't need to install .NET | MIT, with their own third-party notices | Yes (the runtime files next to `OpenPlane.exe`) |

The app needs the Microsoft Edge WebView2 Runtime, which Windows 10 and 11 normally include; it is not shipped with
OpenPlane.

Nuclear Option is made by Shockfront Studios and Flyout by Stonext Games. OpenPlane is not affiliated with either and
ships none of their files. Converted Flyout planes belong to the people who built them.
