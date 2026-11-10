# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

@AGENTS.md

## Comandos

La guía de arriba manda; esto es solo la referencia rápida.

```powershell
# Compilar (solo la compilación; si hay otra sesión espera su turno: timeout de 10 minutos)
dotnet build XtremeDaw.slnx

# Compila, corre comprobar-dsp y deja la copia en pruebas\, que abre el botón Probar del ticket
tools\probar.ps1 -NoAbrir

# No preguntes si la abre: el usuario prueba desde el ticket. Solo si lo pide: la última copia
tools\probar.ps1 -Abrir

# Los tickets (de ahí sale el trabajo) se trabajan con /workbench:revisar y se tocan solo con wb
wb lista
wb ticket 12
```

No hay proyecto de tests: las pruebas son comandos de `xtlab`
(`src\XtremeDaw.Lab\bin\Debug\net10.0-windows\xtlab.exe`), con código de salida distinto de 0 si algo falla.

```powershell
xtlab comprobar-dsp                       # todas las de audio; una fila que falla dice DISTINTO
xtlab midi comprobar                      # un grupo solo
xtlab estirar --comprobar                 # también: escala, extraer-midi, separar
xtlab proyecto verificar-deshacer         # también: -pasos, -clips, -tomas, -importar, -historial, -carpetas, -marcadores, -paquete, -bloqueo, -ab
xtlab componentes comprobar
```

`comprobar-dsp` falla (y con él `probar.ps1`, que dice "NO SE PUEDE PROBAR") si falta `lib\basic-pitch`, que no va en
git: corré `tools\get-basic-pitch.ps1` y compilá de nuevo. La lista completa de comandos de `xtlab` está en
[DETALLES.md](DETALLES.md#xtlab).
