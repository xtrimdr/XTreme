using System.Globalization;
using System.Text;

namespace XtremeDaw.Lab;

static class Program
{
    // ASIO drivers are COM objects: they want an STA thread.
    [STAThread]
    static int Main(string[] argv)
    {
        Console.OutputEncoding = Encoding.UTF8;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        XtremeDaw.Audio.FfmpegSetup.RefreshPath();
        var a = new Args(argv);
        try
        {
            if (!HeavyTurn.Take(a.Command)) return HeavyTurn.BusyCode;
            return a.Command switch
            {
                "dispositivos" => AudioCommands.Devices(),
                "panel" => AudioCommands.Panel(a),
                "latencia" => AudioCommands.Latency(a),
                "grabar" => AudioCommands.Record(a),
                "procesar" => DspCommands.Process(a),
                "mezclar" => DspCommands.Mix(a),
                "exportar" => DspCommands.Export(a),
                "click" => DspCommands.Click(a),
                "comprobar-dsp" => DspCommands.Check(a),
                "tiempos-carga" => ImportChecks.Times(a),
                "samples" => SampleChecks.Run(a),
                "sampler" => SamplerChecks.Run(a),
                "granular" => GranularChecks.Run(a),
                "gm" => GmChecks.Run(a),
                "colas" => TailCommands.Run(a),
                "espectro" => DspCommands.Spectrum(a),
                "lufs" => DspCommands.Lufs(a),
                "masterizar" => DspCommands.Master(a),
                "balance" => TonalChecks.Run(a),
                "afinar" => DspCommands.Tune(a),
                "tempo" => TempoChecks.Run(a),
                "escala" => ScaleChecks.Run(a),
                "extraer-midi" => ToMidiChecks.Run(a),
                "separar" => StemChecks.Run(a),
                "componentes" => ComponentCommands.Run(a),
                "transponer" => TransposeCommands.Run(a),
                "estirar" => StretchChecks.Run(a),
                "looks" => VideoCommands.Looks(a),
                "leer-video" => VideoCommands.ReadVideo(a),
                "nitidez" => DetailCommands.Run(a),
                "camara" => CameraCommands.Run(a),
                "sistema" => SystemCommands.Run(a),
                "version" => VersionCommands.Run(a),
                "reporte" => IssueCommands.Run(a),
                "atajos" => KeyLayoutCommands.Run(a),
                "proyecto" => ProjectCommands.Run(a),
                "midi" => MidiCommands.Run(a),
                "vst" => VstCommands.Run(a),
                "proyectos" => LibraryCommands.Run(a),
                "colaborar" => LiveChecks.Run(a),
                _ => Help(),
            };
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"Error: {e.Message}");
            return 1;
        }
    }

    static int Help()
    {
        Console.WriteLine("""
            xtlab: pruebas del motor de audio de XTreme en .NET

              dispositivos                 lista los drivers ASIO y los dispositivos WASAPI
              panel     [--driver X]       abre el panel del driver ASIO (tamaño de buffer)
              latencia  [--driver X] [--entrada 1] [--salida 1] [--sr 48000]
                                           mide la latencia ida y vuelta (cable de una salida a una entrada)
              grabar    [--driver X] [--segundos 15] [--bpm 100] [--cuenta 4] [--click madera]
                        [--volumen-click 0] [--canal 1|2|estereo] [--monitor]
                        [--sat valvula|cinta] [--vida 0.3] [--salida toma.wav]
                        [--simular [--ruido-2 0.0001]] [--entradas 1,2,3-4]
                                           graba sobre el click; con --monitor te escuchás con la saturación;
                                           --simular graba un tono sin la placa (--ruido-2: la entrada 2 con soplido);
                                           --entradas graba una toma por entrada a la vez, como varias pistas
                                           armadas (toma_e1.wav, toma_e3-4.wav…); con --simular verifica cada WAV
              procesar  entrada.wav [--salida x.wav] [--sat valvula|cinta] [--vida 0.3]
                        [--comp] [--umbral -24] [--ratio 4] [--ataque 10] [--release 150] [--knee 6]
                        [--makeup 0] [--limit] [--comp-estilo rasgueo|arpegio|punteo|id de un preset] [--comp-cant 0.5] [--comp-analizar]
                        [--eq ritmica|punteo|acustica|frente] [--reverb cuarto|sala|salon|placa|catedral]
                        [--delay slapback|corchea|punteada|pingpong|ambiente] [--bpm 120]
                                           aplica efectos de pista y buses a un WAV
              mezclar   carpeta-proyecto|entrada.wav [--salida mezcla.wav] [--segundos N]
                        [--sat valvula|cinta] [--vida 0.3] [--comp] [--comp-estilo id de un preset] [--comp-cant 0.5] [--comp-analizar]
                        [--eq rhythm|lead|acoustic|front] [--cadena vida,comp,eq,puerta]
                                           renderiza la mezcla completa offline (sin la demora del limitador); con un WAV,
                                           --cadena arma los slots de la pista en ese orden (se pueden repetir: eq,vida,eq)
              exportar  [carpeta-proyecto] [--salida nombre] [--carpeta destino] [--calidad alta|estandar|borrador]
                        [--reemplazar] [--prueba] [--audio wav|mp3|flac|aiff|ogg] [--stems] [--stems-buses Reverb,Delay]
                        [--bits 16|24|32] [--frecuencia 44100|48000|96000] [--sin-dither] [--kbps 128..320|vbr]
                        [--cola-fija 4] [--normalizar -1] [--mono]
                        [--destino project|reels|youtube|ig-square|ig-portrait] [--volumen como-esta|youtube|…]
                        [--lufs -14] [--cola 8] [--rango 12-45|1:05-1:30|loop]
                        [--codificador auto|cpu|nvenc|amf|qsv] [--resolucion 720|1080|1440|2160] [--bitrate 20]
                                           exporta el proyecto a MP4 (o la mezcla en WAV / MP3) con mezcla offline y FFmpeg;
                                           --stems deja además un WAV por pista en "<nombre> - stems" (--stems-buses
                                           suma uno por esas carpetas o buses); --bits, --frecuencia, --sin-dither,
                                           --kbps, --cola-fija (segundos exactos), --normalizar (dBTP) y --mono son el
                                           "Avanzado" de Exportar audio (por defecto, lo último que eligió el proyecto);
                                           --destino usa el cuadro de esa red (Reels/Shorts/TikTok 9:16, YouTube 16:9,
                                           Instagram 1:1 y 4:5); el volumen es el Destino del master del proyecto
                                           (--volumen lo cambia para esta exportación; --lufs lleva la mezcla a ese
                                           volumen integrado); --cola deja sonar reverb y delay hasta N s después
                                           del último clip (8 por defecto, 0 = corta ahí); --rango exporta solo ese tramo
                                           (loop = el A–B del proyecto), con su cola después de B. Muestra los LUFS del resultado;
                                           --codificador elige quién codifica el video (auto = la placa NVIDIA, AMD o
                                           Intel si hay, si no el procesador); --resolucion y --bitrate (Mbps) son
                                           el "Avanzado" del diálogo: el lado corto del cuadro y el bitrate del video
              click     [--salida clicks.wav] [--bpm 100]
                                           escribe todos los sonidos de click a un WAV para escucharlos
              colas     [--proyecto carpeta] [--bloque 192] [--segundos 3] [--silencio 120] [--paso 5] [--nivel 0.1] [--caida dB/s]
                                           lo que cuestan por bloque los buses y el master mientras se apagan sus colas;
                                           --caida apaga la entrada de a poco, como la cola de un instrumento
              tiempos-carga archivo        cuánto tarda cada paso de cargar un audio: decodificar, remuestrear y analizar
              lufs      archivo.wav|mp3        volumen integrado (EBU R128) del archivo, como el medidor LUFS del master
              masterizar archivo.wav [--salida x_master.wav] [--cuanto 0.35] [--banda-graves|--banda-medios|--banda-agudos 0..1]
                        [--corte 0|20..150] [--barro dB] [--graves dB] [--presencia dB] [--aire dB] [--inclinacion dB]
                        [--cruce-graves 150] [--cruce-agudos 5000] [--cinta 0..1] [--ancho 0..2] [--graves-mono Hz]
                        [--recorte 0..3] [--empuje 0] [--techo -1] [--release 80] [--sin-limitador]
                        [--volumen youtube|instagram|tiktok|spotify|apple] [--pua 0..1]
                        [--look natural|glue|bright|warm|front [--cuanto-look 1]] [--sugerime]
                                           el mastering de la ventana sobre un WAV: cuánto comprime cada banda
                                           ("Cuánto", 0 = nada), cuánto baja el limitador y los LUFS de entrada y salida;
                                           --volumen es el Destino: mide el archivo entero y usa la ganancia que lo lleva
                                           al volumen de esa red en vez del empuje. Con mucha ganancia el soft clip se
                                           lleva solo una parte (más de 2 dB sobre el techo: la mitad, hasta 4 dB);
                                           --pua es la cuarta banda (baja 2-5 kHz cuando pega la púa); --look pone
                                           un look al --cuanto-look (0 = Natural, 1 = el look entero); --sugerime
                                           muestra qué oye en el archivo y qué propone, y masteriza con eso
              balance   archivo [--objetivo pop|rock|edm|hiphop|acoustic|orchestral|dubstep|riddim|deepdubstep|…] [--referencia a.wav,b.mp3]
                        [--modo estereo|mid|side] [--curva] [--comprobar]
                                           el Balance tonal del archivo entero contra un objetivo: las cuatro bandas
                                           (graves 20-120 Hz, medios graves 120-1k, medios agudos 1k-5k, agudos 5k-20k)
                                           y el punch de los graves (factor de cresta bajo 120 Hz); --referencia arma el
                                           objetivo con esos archivos, como "Referencia…" del panel; --curva lista los
                                           61 puntos (1/6 de octava, suavizado de 1/3) con su rango; --comprobar corre
                                           solo sus comprobaciones (las de comprobar-dsp)
              afinar   archivo.wav [--en segundos] [--umbral -50]
                                           la nota que oye el afinador en ese momento del archivo; --umbral es
                                           la sensibilidad de la ventana (dB, de -70 = muy sensible a -30)
              tempo     archivo [--desde s] [--hasta s]
                                           los BPM del archivo (o de ese tramo), su primer golpe y qué tan seguro
                                           está, como "Detectar tempo" en un clip (el doble y la mitad también valen)
              escala    archivo [--desde s] [--hasta s] [--callado] [--espectro] [--bajo stem.wav]
                                           la escala del archivo (o de ese tramo), sus notas, la afinación y los
                                           acordes con su tiempo, como "Detectar escala y acordes" en un clip;
                                           --espectro no prueba otros métodos; --bajo compara los acordes con ese
                                           stem de bajo; --comprobar corre solo sus comprobaciones
              extraer-midi archivo [--melodia] [--desde s] [--hasta s] [--inicio 0.5] [--nota 0.3]
                        [--min-ms 128] [--midi notas.mid] [--bpm 120]
                                           las notas del archivo (o de ese tramo), como "Extraer MIDI" en un clip
                                           (Basic Pitch); --melodia deja una nota a la vez; --inicio y --nota son
                                           qué tan seguro tiene que estar de que una nota empieza y sigue sonando
              componentes [estado | instalar id [--paquete p] [--url u] | convertir carpeta-th salida | comprobar]
              separar   archivo [--stems 2|4|6] [--calidad rapida|alta|maxima] [--modelo general|porstem|6]
                        [--solo voz,bateria,bajo,guitarra,piano] [--pasadas n] [--sin-desplazar]
                        [--desde s] [--hasta s] [--salida carpeta] [--cancelar s] [--cpu]
                                           separa el archivo en stems y deja un WAV por stem, como "Separar en stems"
                                           en un clip (Demucs); --sin-desplazar hace una sola pasada sin correr el
                                           audio (para comparar con otra versión); --cancelar corta a los s segundos y
                                           dice cuánto tardó en parar; --cpu no usa la placa de video; --modelo, --solo
                                           y --pasadas son el modo Avanzado; --comprobar corre
                                           sus comprobaciones
                                           (más bajo = más notas); --midi las guarda en un .mid
              extraer-midi --comprobar     solo las comprobaciones del extractor (las de comprobar-dsp)
              samples listar carpeta [--buscar "kick 808"]   los samples de una carpeta, como los busca la Biblioteca
              samples leer archivo         decodifica un archivo y dice cómo (nativo o por FFmpeg)
              transponer archivo.wav [--semitonos 2] [--salida x_+2st.wav]
                                           cambia el tono sin cambiar el largo (de -24 a +24, admite fracciones = cents),
                                           como el Transponer de la pista: vocoder de fase PVDR + remuestreo sinc
              estirar archivo.wav [--razon 1.25 | --de 120 --a 100] [--semitonos 0] [--salida x_x1.25.wav] [--comprobar]
                                           cambia el largo sin cambiar el tono, como un clip que sigue el tempo
              espectro  [entrada.wav] [--hz 1000] [--nivel -6]
                                           el espectro en vivo del EQ (FFT de 8192 después del EQ, como la web);
                                           sin WAV, comprueba un seno contra el AnalyserNode de Web Audio
              looks     [--look id] [--salida look.cube] [--blancos 0] [--negros 0] [--luz 0]
                                           lista los looks de color con cómo cambian unos colores de prueba y comprueba
                                           que queden en rango; con --look y --salida escribe ese look como LUT .cube
                                           (none = sin look), con los ajustes Blancos, Negros y Sensación de luz (-100..100)
              leer-video archivo [--ancho 640] [--saltos 20] [--cpu] [--cuadro 12.5 [--salida cuadro.bmp]] [--comparar]
                                           mide el decodificador de la preview (FFmpeg dentro de la app): abrir, leer
                                           seguido, saltar y la memoria por cuadro; --cpu sin la placa de video;
                                           --cuadro guarda ese cuadro; --comparar mide ffmpeg.exe por salto y la
                                           diferencia de imagen con él
              nitidez   [video|imagen] [--en 1] [--nitidez 60] [--claridad 60] [--resplandor 0] [--look hdr|dreamy]
                        [--intensidad 100] [--alto 1080] [--ancho-preview 640] [--carpeta dir]
                                           compara Nitidez, Claridad y Resplandor (y lo que suman los looks HDR y
                                           Soñado) de la exportación (FFmpeg) con los de la preview
                                           sobre el mismo cuadro (de un video o uno de prueba), a tamaño de exportación y
                                           al ancho que muestra la preview (640, menos con zoom); mide cuánto cuesta
                                           por cuadro y deja PNG
              camara    [listar]           lista las cámaras (DirectShow) y sus formatos; * = la que elige la app
              camara grabar [--dispositivo X | --ip 192.168.1.50:4747] [--resolucion native|1080p|720p|480p]
                        [--segundos 5] [--salida camara.mp4]
                                           graba el video como una toma (H.264, 30 fps, keyframe cada 1 s)
              camara verificar-corte [--dispositivo X]
                                           corta una grabación a la mitad (con y sin FFmpeg) y comprueba que el video quede
              sistema   [--segundos 5] [--instalar-ffmpeg]
                                           muestra CPU, GPU y RAM cada 1,5 s como la barra de arriba, si está FFmpeg
                                           y con qué placa de video exporta; --instalar-ffmpeg lo instala antes con
                                           winget, como el aviso de la ventana
              version                      esta versión contra la última release de GitHub (el aviso de "Acerca de")
              reporte enlace "título" [--texto t] [--idea]
                                           el enlace que abre "Enviar comentarios", sin abrir nada
              reporte comprobar            comprueba ese enlace: textos, codificación y largo
              atajos comprobar             ninguna distribución de otro DAW repite una tecla
              colaborar comprobar [--internet]
                                           la edición en vivo entre copias (#291): diferencias, código de invitación y una
                                           sesión real por 127.0.0.1 (cambios en los dos sentidos, audio, deshacer, versión);
                                           con --internet, la misma por el servidor de sesiones (#320, XTREME_RELAY)
              colaborar anfitrion carpeta [--nombre N] [--internet] | unirse código carpeta [--nombre N]
                                           una sesión a mano: invita a un proyecto (por internet, con código corto) o se une
                                           y crea su copia en la carpeta
              proyecto abrir    carpeta    abre un proyecto nativo y muestra pistas, avisos e historial
              proyecto versiones carpeta   lista las versiones guardadas, de más nueva a más vieja
              proyecto volver   carpeta n  restaura la versión n y conserva el estado actual en el historial (tomas y exportaciones quedan)
              proyecto guardar-version carpeta [nombre]   guarda una versión ahora; con nombre no se borra sola
              proyecto nombrar-version carpeta n [nombre] le pone nombre a la versión n (sin nombre se lo saca)
              proyecto verificar-pasos     verifica los nombres de los pasos de Historial, saltar a un paso y las versiones
              proyecto verificar-deshacer  verifica fotos, agrupación de sliders, rehacer y color e ícono de pista sin modificar archivos
              proyecto verificar-clips     verifica anclaje, imanes, recortes, fades y operaciones de pistas
              proyecto clips carpeta       lista los IDs de pistas y clips para editar desde la consola
              proyecto encuadre carpeta clip [--zoom 1.5] [--panx 0.2] [--pany -0.3] [--espejar] [--restaurar]
              proyecto sincronia carpeta clip [--audio-ms -30] [--camara-ms 80] [--heredar]
              proyecto formato  carpeta [--relacion 16:9|9:16|1:1] [--disposicion lado-a-lado|apilado]
                                           como la ventana: 9:16 pasa a apilado y 16:9 a lado a lado
              proyecto editar carpeta mover|recortar-inicio|recortar-final|estirar-inicio|estirar-final|fade-in|fade-out clip delta-segundos
                        [--libre] [--iman 1/4] [--a-clips inicio|final|ambos|no] [--pps 100]   con imán, de a pasos
                        enteros; --a-clips: ese borde se pega al de otro clip a menos de 8 px (a ese zoom), si queda más
                        cerca que el paso de la grilla; sin imán ni --a-clips, o --libre, sin pegarse a nada
              proyecto editar carpeta alinear clip [--iman 1/4]   el comienzo de la toma a la línea del imán más cercana
              proyecto editar carpeta a-pista clip pista [--intercambiar clip] | reordenar desde hasta
              proyecto editar carpeta inicio|fijar-corte|restaurar|quitar-clip clip | quitar-pista pista
              proyecto editar carpeta nueva-pista | agregar-toma medio [--pista pista]
              proyecto editar carpeta borrar-rango pista[,pista…] desde --hasta fin [--cerrar]   saca ese tramo de los clips de
                        esas pistas; queda el hueco, o con --cerrar lo de después se corre y lo tapa
              proyecto editar carpeta al-frente clip   lo pone arriba en su pista: donde se pisan, es el que se ve
              proyecto mezcla carpeta [ab|copiar|olvidar]   el A | B del mixer: pasar a la otra mezcla, copiar esta sobre la
                                           otra u olvidarla (sin nada, dice cuál suena)
              proyecto editar carpeta bloquear|desbloquear clip|pista   un clip (o toda la pista) bloqueado no se mueve, no se
                                           recorta ni se borra, y una toma nueva no lo pisa
              proyecto editar carpeta transicion clip corte|fundido|negro [--segundos 0.5]   la transición que entra a ese clip desde
                                           el que termina donde empieza: funde la imagen y el sonido, centrada en la unión
              proyecto transiciones carpeta   lista dónde se tocan los clips de cada pista y qué transición tiene cada unión
              proyecto editar carpeta nueva-carpeta [nombre] [--pistas id,id] | quitar-carpeta carpeta   agrupa pistas (quitarla las deja sueltas)
              proyecto editar carpeta a-carpeta pista carpeta|raiz [--en n] | sacar-de-carpeta pista | mover-carpeta carpeta n
              proyecto editar carpeta carpeta id [--nombre X] [--plegar] [--ocultar]   renombra y pliega una carpeta (--ocultar: un
                                           separador se oculta o se muestra en el timeline)
              proyecto editar carpeta nuevo-separador [nombre] [--en n] [--visible]   un separador con nombre en ese lugar de la secuencia (oculto en el timeline salvo --visible)
              proyecto editar carpeta lado id izquierda|derecha              de qué lado del mixer va la tira (pista, carpeta, separador o bus)
              proyecto editar carpeta tempo bpm    cambia el tempo con todo en su compás y deja los clips estirados en cache/
              proyecto editar carpeta tempo-mapa [tiempo:bpm,…] [--compas compás:n/d,…]   la pista de tempo: cada punto
                                           en tiempos (negras desde 0), con ~ al final es una rampa hasta el siguiente
                                           ("0:120,16:90~,32:140"); los compases cambian al empezar uno (desde 0:
                                           "0:4/4,8:7/8"). Lo MIDI, lo de pistas que siguen el tempo, marcadores,
                                           acordes y el loop quedan en su tiempo; sin nada, muestra el mapa
              proyecto editar carpeta sigue-tempo pista si|no   una pista de audio que sigue el tempo: sus clips se
                                           mueven y se estiran con el mapa (por defecto quedan en segundos)
              proyecto verificar-tempo     verifica la pista de tempo: rampas, compases, click, qué se mueve, deshacer y .mid
              proyecto editar carpeta transponer pista semitonos   cambia el tono de la pista sin cambiar el largo (-12..+12, 0 = original)
                                           y deja sus tomas transpuestas en cache/ (como el Transponer del inspector)
              proyecto editar carpeta guardar-plantilla pista [nombre] | usar-plantilla id|nombre | aplicar-plantilla pista nombre   plantillas de pista (pistas.json)
              proyecto plantillas-de-pista x   lista las plantillas de pista guardadas
              proyecto verificar-carpetas  verifica el orden de carpetas y pistas, mover adentro y afuera, quitar y deshacer
              proyecto marcadores carpeta [agregar segundos [nombre] | mover id segundos | renombrar id nombre | quitar id | capitulos]
                                           los marcadores del tema (Intro, Solo…); sin operación, los lista
              proyecto repetir carpeta [desde hasta | --quitar]   el rango A–B que repite el loop; sin nada, lo muestra
              proyecto duracion carpeta           cuánto dura el tema y si entra en cada red (historias, X, Reels, TikTok)
              proyecto verificar-marcadores verifica orden, nombres, saltos, deshacer y guardado de marcadores y del rango A–B
              proyecto verificar-paquete   empaqueta un proyecto con una toma de afuera y lo desempaqueta: lo que suena, el respaldo y un ZIP tramposo
              proyecto verificar-guardar-como   el archivo .xtp y Guardar como: el formato anterior, renombrar, rescatar del .bak, copiar a otra carpeta
              proyecto verificar-bucles    clips, clips MIDI y bloques de automatización en bucle: repeticiones, seguir al original, dividir, guardar
              proyecto verificar-bloqueo   la regla del bloqueo: un clip o una pista bloqueados no se mueven, recortan ni borran, y grabar no los pisa
              proyecto verificar-sin-guardar   el proyecto sin guardar: dónde vive, recientes, recuperarlo al arrancar y Guardar como
              proyecto verificar-ab        el A/B de la mezcla: ida y vuelta, mute y solo quietos, pistas y buses nuevos, deshacer
              proyecto editar carpeta ganancia clip dB | curva clip "seg:dB,..."   volumen del clip y su curva (seg desde el inicio de la toma)
              proyecto verificar-tomas     verifica a qué pista va una toma nueva, su nombre y su canal
              proyecto importar-audio carpeta archivo [--en segundos] [--pista id] [--vincular]   copia un audio de afuera a media/ (o lo deja donde está) y lo pone en una pista
              proyecto verificar-importar  verifica la importación de audio (formato, nombres, archivos rotos)
              proyecto verificar-vinculados copiar al proyecto, archivo movido, reencontrar y morir a mitad de copia
              proyecto vinculados carpeta   lista las tomas y dónde están sus archivos
              proyecto copiar-vinculado carpeta medio   copia a media/ lo que la toma tiene afuera
              proyecto reencontrar carpeta medio --archivo X | carpeta [medio] --buscar-en Y   vuelve a encontrar lo que falta
              proyecto cortar-copia carpeta medio [--al-final]   copia y mata el proceso a la mitad (o al terminar, sin guardar)
              proyecto insertos carpeta pista [agregar vida|comp|eq|puerta|reverb|delay|limpieza|multibanda|color|cinta|ancho|softclip|limitador|chorus|flanger|phaser|tremolo|bombeo [--en n] | agregar vst --plugin "nombre" | quitar n | mover n m | duplicar n | prender n | apagar n | plegar n]
                                           los slots de efectos de la pista (número o id), en el orden en que suenan; sin
                                           operación, los lista. Como el menú de cada efecto en la tira ancha del mixer;
                                           plegar n lo pliega o despliega en el mixer (no cambia el sonido)
              proyecto insertos carpeta --bus id|n [agregar … | quitar n | …], o carpeta [agregar … | cadena | quitar n | …] --master
                                           lo mismo para un bus o el master
              proyecto insertos carpeta --carpeta id|n|nombre [agregar … | quitar n | …]
                                           lo mismo para una carpeta: sus efectos van sobre la suma de sus pistas
              proyecto buses carpeta [agregar [nombre] [--tipo reverb|delay|vacio] | quitar n | renombrar n nombre |
                        volumen n 0.8 | paneo n -0.3 | mute n | color n #rrggbb|ninguno | icono n id|ninguno]
                                           los buses, en el orden en que suenan, con sus slots, su salida y sus envíos;
                                           uno nuevo trae una reverb, un delay o nada; quitar uno se lleva los envíos que
                                           le llegaban y lo que salía a él va al master
              proyecto buses carpeta --carpeta id|n|nombre [volumen 0.8 | paneo -0.3 | mute | solo]
                                           el canal de una carpeta: fader, paneo, M y S (su solo deja sonar solo sus
                                           pistas); sin operación, lo muestra con sus slots y envíos
              proyecto envios carpeta quien [bus [nivel] [--pre|--post] [--apagar|--prender] [--quitar|--sumar]] [--salida bus|master]
                                           la salida y los envíos de una pista (número o id), carpeta:n o bus:n; --pre lo
                                           saca antes del fader (después de los efectos y el paneo). Un bus manda a otros buses
                                           (no a sí mismo ni cerrando un bucle) y sale solo al master. --quitar saca el bus de su lista (apagado, con su nivel) y
                                           --sumar lo vuelve a poner. Las opciones van después del nivel
              proyecto historial carpeta   tomas (en uso / no usadas), exportaciones y actividad, como la pestaña Historial
              proyecto historial carpeta quitar|usar medio [--en s] [--pista id] | borrar-no-usadas | borrar-exportacion n
              proyecto verificar-historial verifica el registro de tomas, la Papelera y las frases de la actividad
              proyecto toma     carpeta [--segundos 3] [--pista id] [--camara]
                                           graba un tono como la ventana (--pista: la pista seleccionada;
                                           --camara: con el video de la cámara que elige la app)
              proyecto cortar   carpeta [--segundos 3] [--camara]
                                           graba un tono (y el video) y corta sin cerrar, para probar la recuperación
              midi ver carpeta             las pistas MIDI con sus clips y sus notas
              midi pista carpeta           agrega una pista MIDI (suena con un tono de prueba hasta que tenga instrumento)
              midi clip carpeta [--pista n|id] [--en segundos] [--compases 4]   un clip MIDI vacío en esa pista
              midi nota carpeta --clip id [--altura C4|60] [--pulso 0] [--largo 1] [--vel 100]
                                           una nota en el clip (pulso y largo en tiempos desde su comienzo)
              midi acordes carpeta --clip id   reemplaza sus notas por Do, Sol, Lam, Fa (un compás cada uno)
              midi acorde carpeta --clip id [--raiz C4] [--tipo scale] [--inversion 0] [--pulso 0] [--largo 1] [--vel 100]
                                           un acorde en el clip; scale y scale7 lo sacan de la escala del proyecto
              midi escala carpeta [--raiz C4] [--escala minor]   la escala del proyecto; sin --escala, ninguna
              midi ajustar-escala carpeta --clip id   lleva a la escala del proyecto las notas que quedaron afuera
              midi quitar carpeta --clip id    saca el clip MIDI
              midi exportar-mid carpeta --clip id --a x.mid   el clip como archivo MIDI (tipo 1, al tempo del proyecto)
              midi importar-mid carpeta --de x.mid [--pista n|id] [--en segundos]
                                           con --pista, todo en un clip de esa pista MIDI; sin ella, una pista nueva
                                           por cada pista del archivo
              midi partitura carpeta --clip id --a x.pdf   el clip como partitura en PDF (doble pentagrama, 4/4, armadura
                                           de la escala del proyecto)
              midi patron carpeta --clip id [--nombre x] [--compases 4] [--repetir 2]   hace del clip un patrón; --repetir, cuántas vueltas dura
              midi patron carpeta --clips a,b [--separados]   varios clips, un patrón del largo de todos juntos (una parte por pista) o uno por clip
              midi patron-insertar carpeta --clip id --pista n [--en segundos]   un clip más del patrón de ese clip
              midi patron-instrumento carpeta --clip id --pista n   suma al patrón un instrumento en esa pista (con --quitar, saca el del clip)
              midi patron-unico carpeta --clip id   saca el clip del patrón (suena igual)
              midi patron-disolver carpeta --clip id   termina el patrón de ese clip: todos sus clips quedan únicos
              midi patron-borrar carpeta --clip id   borra el patrón de ese clip con todos sus clips
              midi cuantizar carpeta --clip id [--grilla bar|1/2…1/64|1/2T…1/32T|1/2D…1/16D]
                                           mueve el comienzo de sus notas a la grilla de la canción (el largo queda)
              midi velocidad carpeta --clip id [--rango 100]
                                           escala las velocidades alrededor de su promedio (50 las acerca, 0 las
                                           empareja, 150 les da más dinámica)
              midi humanizar carpeta --clip id [--cantidad 10] [--semilla n]
                                           mueve cada velocidad al azar hasta ± cantidad
              midi entradas                los teclados y controladores MIDI que ve Windows, con sus ajustes de
                                           Configuración → MIDI (también los que están desconectados)
              midi cortar carpeta                     graba una toma MIDI de prueba y mata el proceso a la mitad
              midi escuchar [--entrada nombre] [--segundos 10]
                                           abre las entradas como la ventana e imprime lo que llega, y lo que toma
                                           el motor (después del canal y la curva de velocidad)
              midi instrumento carpeta [--pista n] --plugin "nombre" | --quitar
              midi sampler carpeta [--pista n] --sample archivo | --quitar
                                           la pista toca ese archivo con el sampler nativo
              sampler archivo [--nota 60] [--raiz n] [--segundos 2] [--filtro lp24] [--corte 2000] [--salida x.wav]
                                           toca un archivo con el sampler, en una nota
              midi granular carpeta [--pista n] --sample archivo | --quitar
                                           la pista toca ese archivo con el granular nativo
              granular archivo [--nota 60] [--segundos 4] [--modo stretch|scrub] [--posicion 0] [--velocidad 1]
                       [--densidad 20] [--tamano 0.08] [--spray 0] [--jitter 0] [--salida x.wav]
                                           toca un archivo con el granular, en una nota
              midi gm carpeta [--pista n] [--programa 0] [--bateria] | --quitar
                                           la pista toca con el instrumento General MIDI nativo
              gm presets                   lista los presets del banco GM de la app (lib\gm, tools\get-gm.ps1)
              gm [--programa 0] [--bateria] [--nota 60] [--segundos 2] [--salida x.wav]
                                           toca una nota del banco GM
                                           el instrumento VST3 que toca la pista (de vst escanear); --quitar vuelve al
                                           tono de prueba. xtlab mezclar lo renderiza con el plugin
              midi comprobar               verifica el sonido de las notas (bloques, release, exportación, tempo) y el
                                           teclado (suena como la nota escrita, pedal, tomas, cuantizar, velocidad)
                                           (xtlab mezclar carpeta renderiza el proyecto con sus pistas MIDI)
              vst escanear [--todo] [--carpeta X] [--archivo X]
                                           busca los plugins VST3 en las carpetas de vst carpetas (los guarda en
                                           plugins.json; --todo los abre todos de nuevo, --archivo reintenta uno solo)
              vst lista                    los plugins encontrados, instrumentos y efectos (y cuáles están ocultos)
              vst carpetas [--agregar X] [--sacar X]
                                           dónde busca: las de Windows y las que sumaste (como Configuración → Plugins)
              vst ocultar|mostrar "nombre" lo saca de las listas de la app o lo vuelve a poner
              vst probar "nombre" [--segundos 4] [--salida x.wav] [--ventana]
                                           toca un acorde con ese plugin a un WAV, como la exportación (--ventana abre
                                           antes su ventana y usa lo que dejes)
              vst bucle "nombre" [--bloque 256] [--vueltas 4]
                                           un bucle en un instrumento: el primer tiempo de cada vuelta tiene que sonar a
                                           tiempo, y una nota que cruza B no queda prendida
              vst efecto "nombre" [--entrada x.wav] [--segundos 4] [--bloque 256] [--tarde] [--toma] [--salida x.wav] [--apagado]
              vst estado "nombre"          carga el estado del plugin en una copia nueva y comprueba que vuelve igual
              vst parametros "nombre" [--mover "parámetro"] [--segundos 2]
                                           pasa audio (o acordes de prueba) por un efecto como la exportación y en vivo
                                           en bloques del driver; comprueba que en vivo llega justo un bloque después
                                           (--tarde: el plugin siempre llega tarde; sale entero, un bloque más tarde)
                                           (--toma: como durante una grabación: empieza con el bloque extra y la demora no se mueve)
              proyectos lista|plantillas [--raiz X]
              proyectos nuevo "Nombre" [--plantilla id] | renombrar carpeta "Nombre" | duplicar carpeta
              proyectos borrar carpeta | guardar-plantilla carpeta "Nombre"
              proyectos empaquetar carpeta [--completo] [--salida x.zip] | desempaquetar x.zip
                                           el proyecto en un ZIP (solo lo que suena, o con --completo todas las tomas,
                                           versiones y exportaciones) y de vuelta a un proyecto nuevo
              proyectos previsualizar "Nombre" | olvidar carpeta | carpeta ruta
              proyectos buscar carpeta     qué abre "Abrir carpeta" con esa carpeta (el proyecto, uno adentro, vacía, de la web…)
              proyectos borrar-plantilla id
              proyectos plantillas-video | guardar-plantilla-video carpeta "Nombre"
              proyectos aplicar-plantilla-video carpeta id|"Nombre" | borrar-plantilla-video id
                                           las plantillas de video (filtro, ajustes y efectos) de la pestaña Color
              proyectos nuevo "Nombre" [--plantilla id] [--carpeta vacía]
                                           el gestor de proyectos sin la ventana (--raiz: otra carpeta de proyectos)
                                           --datos X: configuración, recientes y plantillas aislados para pruebas
            """);
        return 0;
    }
}

sealed class Args
{
    readonly Dictionary<string, string?> opts = new(StringComparer.OrdinalIgnoreCase);
    public string Command { get; } = "";
    public List<string> Positional { get; } = [];

    public Args(string[] argv)
    {
        for (int i = 0; i < argv.Length; i++)
        {
            string s = argv[i];
            if (s.StartsWith("--"))
            {
                string key = s[2..];
                string? value = i + 1 < argv.Length && !argv[i + 1].StartsWith("--") ? argv[++i] : null;
                opts[key] = value;
            }
            else if (Command == "") Command = s.ToLowerInvariant();
            else Positional.Add(s);
        }
    }

    public bool Flag(string key) => opts.ContainsKey(key);

    public string? Get(string key) => opts.TryGetValue(key, out var v) ? v : null;

    public string Get(string key, string fallback) => Get(key) ?? fallback;

    public double Double(string key, double fallback)
    {
        var v = Get(key);
        if (v == null) return fallback;
        return double.TryParse(v.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            ? d
            : throw new ArgumentException($"--{key}: \"{v}\" no es un número.");
    }

    public int Int(string key, int fallback) => (int)Math.Round(Double(key, fallback));
}
