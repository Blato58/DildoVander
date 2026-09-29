# Hostitelský simulátor výběru

Simulátor E2.3 spouští čistý C++ model `DeviceControl` a `SelectionSequence`
proti fake portu a journalu. Každý řádek časové osy začíná `SIMULACE`.
Požadavky na motor a západku neovládají hardware.

Z kořene repozitáře sestavte čtyři zdroje C++14 do dočasné složky. Na Windows
ve Visual Studio Developer Command Prompt například:

```text
cl /nologo /std:c++14 /W4 /WX /EHsc kufr\firmware\src\features\safety\SafetyController.cpp kufr\firmware\src\features\wand_selection\SelectionSequence.cpp kufr\firmware\src\features\device_control\DeviceControl.cpp kufr\simulator\main.cpp /Fe:<docasna-slozka>\simulator.exe
node kufr\simulator\verify-scenarios.mjs <docasna-slozka>\simulator.exe
```

Ověřovač bere číslo přihrádky a ID povelu ze sdílené platné E2.1 fixture.
Spouští šest deterministických scénářů: běžný výběr, ztracený ACK se dvěma
opakováními, Stop, ztrátu lease, restart při uvolňování a chybu zápisu journalu.
Kontroluje časovou osu, nejvýše jedno uvolnění, stav journalu a dostupnost
přihrádky. Tato fixture se v tomto kroku nepředává přes JSON parser do C++;
propojení syrového protokolu a modelu je další integrační práce E2.

Ruční běh: `<docasna-slozka>\simulator.exe normal 4 <commandId>`.
Výstup je pouze simulace. Neověřuje fyzické vypnutí, NVS atomicitu, TLS,
skutečné WSS spojení ani časování na ESP32.
