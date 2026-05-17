# Hostix Embedded Tools Directory

## Quick Start (No PHP? Place it here)
Place a portable PHP binary at:
  tools/php/php.exe

Download PHP for Windows (no installer needed):
  https://windows.php.net/download/
  ? Download: PHP 8.3 VS16 x64 Thread Safe ? extract ZIP here.

## Auto-managed tools:
- tools/adminer/adminer.php     ? downloaded automatically on first panel open
- tools/php/php.exe             ? place portable PHP here for zero-dependency mode
- tools/pgadmin/                ? future
- tools/redisinsight/           ? future
- tools/mongo-express/          ? future

## How Hostix finds PHP (in order):
1. tools/php/php.exe            (embedded — preferred)
2. C:\laragon\bin\php\...
3. C:\xampp\php\php.exe
4. C:\wamp64\bin\php\...
5. C:\php\php.exe
6. System PATH (where php)
