#!/usr/bin/env bash
set -euo pipefail
export MSYS2_ENV_CONV_EXCL=PERL5LIB
export PATH='/c/Users/Lenovo/pc-native/winlibs/mingw64/bin:/usr/bin:/bin'
export PERL5LIB='/c/Users/Lenovo/pc-native/ExtUtils-MakeMaker-7.78/lib:/c/Users/Lenovo/pc-native/Locale-Maketext-Simple-0.21/lib:/c/Users/Lenovo/pc-native/Pod-Escapes-1.07/lib:/c/Users/Lenovo/pc-native/Pod-Perldoc-3.28/lib:/c/Users/Lenovo/pc-native/Pod-Simple-3.48/lib:/c/Users/Lenovo/pc-native/Pod-Usage-2.05/lib:/c/Users/Lenovo/pc-native/podlators-v6.1.1/lib'
cd /c/Users/Lenovo/pc-native/openssl-3.0.15
gcc --version
perl ./Configure mingw64
mingw32-make.exe -j2 build_libs
