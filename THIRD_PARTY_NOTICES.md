# Attribution

## R_F's SAIN zzap work

COMET adapts combat features from R_F (danyhappy564-cmyk), with his explicit permission to reuse his contributions and publish this separate mod. His developer kit also informed regression tests. COMET adds its own integration, settings, diagnostics and changes.

The behavior baseline is documented in [SOURCE_BASELINE.md](SOURCE_BASELINE.md). R_F's work remains credited wherever it is adapted; it is not claimed as original work by Chazut.

## liquidwarp's SAIN fixes

COMET adapts corrections and findings from [liquidwarp/SPT-SAIN](https://github.com/liquidwarp/SPT-SAIN), distributed under the SAIN MIT license reproduced below. Credit for these original fixes belongs to liquidwarp:

- [Raycast lengths and grenade ray direction](https://github.com/liquidwarp/SPT-SAIN/commit/0eec6371e7fe78b3c17b89abb79e44ade015c4f8).
- [Enemy activity and identity checks](https://github.com/liquidwarp/SPT-SAIN/commit/fc38b64ac6a871031328aa4465d1bc8c57a97a23).
- [Surgery started outside SAIN's layer](https://github.com/liquidwarp/SPT-SAIN/commit/f2c4db5ee3c2a09328d80324a5bc5694eecc3616).
- [Signed recoil and update-independent decay](https://github.com/liquidwarp/SPT-SAIN/commit/8bc11748db7a1b1d775edce32ddb9a221bec01b0).
- [Pose and lean timing](https://github.com/liquidwarp/SPT-SAIN/commit/7563d1f59f9adcd7f2dd272c2f6daca9b76a22a2).
- [Path position cache ordering](https://github.com/liquidwarp/SPT-SAIN/commit/2a402ece56bde6bbc20c79212eff1705bad42e9e).

The [smoke grenade report](https://github.com/liquidwarp/SPT-SAIN/commit/3214db775e58b8c6c85db2e596f93dee7a08c449) also informed regression review; COMET already handled ordinary smoke and custom CS gas separately. No second smoke implementation was added.

Adaptations are in `Client/LiquidwarpFixesAdapter.cs`, `Client/NativeTimingAdapter.cs` and `Client/ExternalSurgeryAdapter.cs`. COMET keeps its supported-bot scope and zzap surgery decisions, retains the native/BHF operation and callbacks, and applies grenade prediction corrections per supported observer rather than globally. Existing COMET flashlight/laser fixes are retained.

## SAIN

SAIN is by Solarint and its contributors: https://github.com/ArchangelWTF/SAIN

COMET requires the original SAIN rather than distributing a modified SAIN binary. The following upstream notice is retained for source-derived portions:

```text
MIT License

Copyright (c) 2026 Solarint

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

Game binaries and dependency DLLs are not included in this repository. Their respective licenses and notices remain applicable.
