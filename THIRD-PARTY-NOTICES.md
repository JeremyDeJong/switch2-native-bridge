# Third-party notices

## HIDMaestro

`lib/HIDMaestro.Core.dll` (redistributed unmodified) and `switch2-pro-composite.json` (the
`switch2-pro-controller-composite` profile, which the bridge loads with its own id and flash image)
come from [HIDMaestro](https://github.com/hifihedgehog/HIDMaestro) v2.1.0, MIT licensed. Its full
license is in `lib/HIDMaestro-LICENSE.txt` and its own third-party notices (including usbip-win2,
BSD-2-Clause) in `lib/HIDMaestro-THIRD-PARTY-NOTICES.txt`.

## switch2controllerpc

The Bluetooth protocol handling in `SwitchController.cs` (GATT UUIDs, command framing, memory
reads, pairing sequence and keys, feature enable, rumble framing) follows
[CareyScott/switch2controllerpc](https://github.com/CareyScott/switch2controllerpc), which builds
on Nadeflore/switch2-controllers and TommyWabg/switch2-controllers-windows10-gyro.

```
MIT License

Copyright (c) 2026 Scott Carey

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
