# Third-party notices

BlueApex itself is MIT-licensed (see LICENSE). It builds on and, when distributed
self-contained, redistributes the following.

## .NET runtime, WPF, Windows Forms

Copyright (c) .NET Foundation and Contributors. MIT License.
https://github.com/dotnet/runtime · https://github.com/dotnet/wpf · https://github.com/dotnet/winforms

## Microsoft Edge WebView2 SDK

`Microsoft.Web.WebView2.*.dll` — Copyright (c) Microsoft Corporation. Distributed under the terms of the
Microsoft.Web.WebView2 NuGet package license (https://www.nuget.org/packages/Microsoft.Web.WebView2).
The WebView2 *runtime* is not redistributed; the installer downloads Microsoft's bootstrapper when a PC lacks it.

## Inno Setup (build tool only)

The installer is built with Inno Setup (Copyright (c) Jordan Russell, Inno Setup License). Its files are not
part of BlueApex; only the generated setup program is distributed.

## C#/WinRT and the Windows SDK .NET projection (samples/MediaWidget)

`WinRT.Runtime.dll` — Copyright (c) Microsoft Corporation. MIT License. https://github.com/microsoft/CsWinRT
`Microsoft.Windows.SDK.NET.dll` — distributed under the terms of the Microsoft.Windows.SDK.NET.Ref NuGet package
license (see https://www.nuget.org/packages/Microsoft.Windows.SDK.NET.Ref). Shipped only with the media widget sample.

## Open-Meteo (samples/WeatherWidget)

Weather data by Open-Meteo.com, licensed under CC BY 4.0 (https://open-meteo.com/en/license).
Free for non-commercial use; attribution required. The sample widget shows the attribution.

## Icons

The tray icon is currently a stock Windows system icon (`SystemIcons.Application`); replace it with the
project's own icon before public distribution.
