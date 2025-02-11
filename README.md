# GNS-Practice

My practices on using [GameNetworkingSockets](https://github.com/ValveSoftware/GameNetworkingSockets), along with my fork of [ValveSockets-CSharp](https://github.com/copyrat90/ValveSockets-CSharp).

Currently, only supports building on Windows with MSVC.

## Build

1. Build GameNetworkingSockets and my C++ examples first with CMake and Ninja (single-config), on the `build/` directory.
    * Refer to the [`BUILDING.md`](https://github.com/ValveSoftware/GameNetworkingSockets/blob/master/BUILDING.md) on GameNetworkingSockets for details.
    * It would be handy to create your own configuration preset in `CMakeUserPresets.json`,\
      inheriting from a preset in the [`CMakePresets.json`](CMakePresets.json). e.g:
        ```json
        {
            "version": 6,
            "configurePresets": [
                {
                    "name": "my-preset",
                    "inherits": "gns-prac-msvc",
                    "cacheVariables": {
                        "CMAKE_TOOLCHAIN_FILE": "C:/vcpkg/scripts/buildsystems/vcpkg.cmake"
                    }
                }
            ]
        }
        ```
1. Build my C# projects afterwards.
    * As it relies on the native `GameNetworkingSockets.dll`, building it with C++ beforehand is a MUST;\
      Otherwise, you'll get runtime error about missing dlls.
