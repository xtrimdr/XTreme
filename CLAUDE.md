# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

@AGENTS.md

## Comandos

La guía de arriba manda; esto es solo la referencia rápida.

```powershell
# Compilar (solo la compilación; si hay otra sesión espera su turno: timeout de 10 minutos)
dotnet build XtremeDaw.slnx

# Al terminar cada cambio: compila, corre comprobar-dsp, deja la copia en pruebas\ y lo anota en el Banco de pruebas
tools\probar.ps1 -NoAbrir "qué probar"
# Un cambio del Banco solo (XT-WorkBench, xtlab, herramientas): compila el Banco y xtlab, sin copia de XTreme
tools\probar.ps1 -NoAbrir -Tema banco "qué probar"

# No preguntes si lo abre: queda en la lista. Solo si el usuario lo pide: la última copia con la lista a la vista
tools\probar.ps1 -Abrir
# O el Banco de pruebas solo, sin XTreme (xtbench.exe, un programa aparte en pruebas\banco\: compilar no lo cierra)
tools\probar.ps1 -Banco

# Los pedidos del usuario (de ahí sale el trabajo), qué falta probar y qué marcó como que no anda o anda mal
tools\probar.ps1 -Lista

# Un pedido o algo marcado (No anda, Anda mal, A mejorar): tomarlo antes de empezar (código 2 = lo tiene otra sesión),
# y al terminarlo vuelve a Pendiente con su número. Varios van juntos (60,61): una sola compilación y copia.
# Lo pedido por chat se anota primero.
tools\probar.ps1 -Toma 12
tools\probar.ps1 -NoAbrir -Arregla 12
tools\probar.ps1 -Pide "qué cambiar" -Tipo error   # error | mejora | funcion | idea
# Cada ticket lleva su tema (lista fija: xtlab lista temas): -Tema mixer al anotar, o a uno que ya está:
tools\probar.ps1 -Clasifica 12 -Tema mixer
# Y el tipo, que el Banco no pregunta (los pedidos llegan sin tipo):
tools\probar.ps1 -Clasifica 12 -Tipo mejora
# Y su título corto, que pone el agente al tomarlo:
tools\probar.ps1 -Titula 12 "Fader del bus no guarda el valor"
```

No hay proyecto de tests: las pruebas son comandos de `xtlab`
(`src\XtremeDaw.Lab\bin\Debug\net10.0-windows\xtlab.exe`), con código de salida distinto de 0 si algo falla.

```powershell
xtlab comprobar-dsp                       # todas las de audio; una fila que falla dice DISTINTO
xtlab midi comprobar                      # un grupo solo
xtlab estirar --comprobar                 # también: escala, extraer-midi, separar
xtlab proyecto verificar-deshacer         # también: -pasos, -clips, -tomas, -importar, -historial, -carpetas, -marcadores, -paquete, -bloqueo, -ab
xtlab componentes comprobar
xtlab lista comprobar
```

`comprobar-dsp` falla (y con él `probar.ps1`, que dice "NO SE PUEDE PROBAR") si falta `lib\basic-pitch`, que no va en
git: corré `tools\get-basic-pitch.ps1` y compilá de nuevo. La lista completa de comandos de `xtlab` está en
[DETALLES.md](DETALLES.md#xtlab).
