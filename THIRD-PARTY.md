# Third-party code

## Vintage Kinematics

Parts of the moving-block implementation follow the approach used by
[Vintage Kinematics](https://github.com/Garward/VintageStoryModding) —
specifically the lift/settle structure of `EntityVKContraption`, the block entity
payload restore (rewriting `posx`/`posy`/`posz` in the saved attribute tree), the
face-connected pruning of a captured selection, and the empty-shape trick for
entities drawn by a custom renderer.

Vintage Kinematics is MIT licensed:

```
MIT License

Copyright (c) 2026 garward

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
