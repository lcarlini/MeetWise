namespace MeetWise.App;

/// <summary>
/// Texts added after the first three pages: the "Try it" test benches and the tooltips. Kept apart from <see cref="Loc"/> so each line carries its
/// English, Portuguese and Spanish together; <see cref="Merge"/> folds them into the main dictionary.
/// </summary>
internal static partial class LocExtra
{
    private static readonly List<(string Key, string En, string Pt, string Es)> Items = new();

    private static void A(string key, string en, string pt, string es) => Items.Add((key, en, pt, es));

    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Merge(Dictionary<string, IReadOnlyDictionary<string, string>> main)
    {
        TryTexts();
        Tooltips();
        PageTexts();
        QuickTexts();
        foreach (var (key, en, pt, es) in Items)
        {
            ((Dictionary<string, string>)main["en"])[key] = en;
            ((Dictionary<string, string>)main["pt"])[key] = pt;
            ((Dictionary<string, string>)main["es"])[key] = es;
        }
        return main;
    }

    private static void TryTexts()
    {
        A("try.title", "Try it yourself: record a clip and compare", "Teste você mesmo: grave um trecho e compare", "Pruébalo tú mismo: graba un fragmento y compara");
        A("try.intro", "Record a few seconds (or open a file), run the test, then listen to the original and the result and read the numbers. The same clip is used by every test in the app.",
            "Grave alguns segundos (ou abra um arquivo), execute o teste, ouça o original e o resultado e leia os números. O mesmo trecho vale para todos os testes do app.",
            "Graba unos segundos (o abre un archivo), ejecuta la prueba, escucha el original y el resultado y lee los números. El mismo fragmento sirve para todas las pruebas de la app.");
        A("try.test", "Test", "Teste", "Prueba");
        A("try.language", "Language", "Idioma", "Idioma");
        A("try.record", "● Record", "● Gravar", "● Grabar");
        A("try.stoprec", "■ Stop ({0} s)", "■ Parar ({0} s)", "■ Parar ({0} s)");
        A("try.seconds", "{0} seconds", "{0} segundos", "{0} segundos");
        A("try.loopback", "Also record what the PC plays (for the echo test)", "Gravar também o que o PC toca (para o teste de eco)", "Grabar también lo que reproduce el PC (para la prueba de eco)");
        A("try.open", "Open a file…", "Abrir um arquivo…", "Abrir un archivo…");
        A("try.playclip", "▶ Play my clip", "▶ Ouvir meu trecho", "▶ Escuchar mi fragmento");
        A("try.stop", "■ Stop playing", "■ Parar áudio", "■ Detener audio");
        A("try.run", "Run test", "Executar teste", "Ejecutar prueba");
        A("try.save", "Save result to report", "Salvar resultado no relatório", "Guardar resultado en el informe");
        A("try.folder", "Open report folder", "Abrir pasta do relatório", "Abrir carpeta del informe");
        A("cmp.title", "Hear the difference", "Ouça a diferença", "Escucha la diferencia");
        A("cmp.hint", "Press the button and talk for 10 seconds, ideally with some noise around you (music, typing, a fan). Then play the original and the result with the settings above.",
            "Pressione o botão e fale por 10 segundos, de preferência com algum ruído por perto (música, digitação, ventilador). Depois ouça o original e o resultado com as configurações acima.",
            "Pulsa el botón y habla 10 segundos, mejor con algo de ruido alrededor (música, teclado, ventilador). Luego escucha el original y el resultado con los ajustes de arriba.");
        A("cmp.record", "● Record 10 s and compare", "● Gravar 10 s e comparar", "● Grabar 10 s y comparar");
        A("cmp.stop", "■ Stop ({0} s)", "■ Parar ({0} s)", "■ Parar ({0} s)");
        A("cmp.original", "▶ Original", "▶ Original", "▶ Original");
        A("cmp.cleaned", "▶ With my settings", "▶ Com minhas configurações", "▶ Con mis ajustes");
        A("cmp.speak", "Recording… talk now.", "Gravando… fale agora.", "Grabando… habla ahora.");
        A("cmp.applying", "Applying your settings…", "Aplicando suas configurações…", "Aplicando tus ajustes…");
        A("cmp.settings", "Applied: {0} · echo cancellation {1}", "Aplicado: {0} · cancelamento de eco {1}", "Aplicado: {0} · cancelación de eco {1}");
        A("cmp.voicelock", "voice lock", "trava de voz", "bloqueo de voz");
        A("cmp.on", "on", "ligado", "activado");
        A("cmp.off", "off", "desligado", "desactivado");
        A("cmp.echo.noref", "on (nothing was playing, so there was no echo to remove)", "ligado (nada estava tocando, então não havia eco a remover)", "activado (no sonaba nada, así que no había eco que quitar)");
        A("cmp.result", "Background {0} dB quieter · your voice {1} dB · uses {2} of real time. Press ▶ to listen.", "Fundo {0} dB mais baixo · sua voz {1} dB · usa {2} do tempo real. Pressione ▶ para ouvir.", "Fondo {0} dB más bajo · tu voz {1} dB · usa {2} del tiempo real. Pulsa ▶ para escuchar.");
        A("tip.cmp.record", "Records your real microphone for 10 seconds (press again to stop early) and applies the noise, echo and voice-lock settings shown above.", "Grava seu microfone real por 10 segundos (pressione de novo para parar antes) e aplica as configurações de ruído, eco e trava de voz acima.", "Graba tu micrófono real 10 segundos (pulsa otra vez para parar antes) y aplica los ajustes de ruido, eco y bloqueo de voz de arriba.");
        A("tip.cmp.original", "Play what the microphone heard.", "Ouça o que o microfone captou.", "Escucha lo que captó el micrófono.");
        A("tip.cmp.cleaned", "Play the same clip as apps receive it through the MeetWise Microphone.", "Ouça o mesmo trecho como os apps o recebem pelo Microfone MeetWise.", "Escucha el mismo fragmento como lo reciben las apps por el Micrófono MeetWise.");
        A("try.src.recording", "recording", "gravação", "grabación");
        A("try.src.previous", "previous recording", "gravação anterior", "grabación anterior");
        A("try.noclip", "No clip yet. Press Record, or open a file.", "Nenhum trecho ainda. Pressione Gravar ou abra um arquivo.", "Aún no hay fragmento. Pulsa Grabar o abre un archivo.");
        A("try.clipinfo", "Clip: {0} s from {1} ({2})", "Trecho: {0} s de {1} ({2})", "Fragmento: {0} s de {1} ({2})");
        A("try.nosystem", "microphone only", "somente microfone", "solo micrófono");
        A("try.withsystem", "microphone + what the PC played", "microfone + o que o PC tocou", "micrófono + lo que reprodujo el PC");
        A("try.speaknow", "Recording… speak normally. Press Stop to finish early.", "Gravando… fale normalmente. Pressione Parar para terminar antes.", "Grabando… habla con normalidad. Pulsa Parar para terminar antes.");
        A("try.tooshort", "That clip is too short. Try again.", "Esse trecho ficou curto demais. Tente de novo.", "Ese fragmento es demasiado corto. Inténtalo de nuevo.");
        A("try.recorded", "Clip saved. Pick a test and press Run test.", "Trecho salvo. Escolha um teste e pressione Executar teste.", "Fragmento guardado. Elige una prueba y pulsa Ejecutar prueba.");
        A("try.loading", "Reading the file…", "Lendo o arquivo…", "Leyendo el archivo…");
        A("try.loaded", "File loaded ({0} s used).", "Arquivo carregado ({0} s usados).", "Archivo cargado ({0} s usados).");
        A("try.cantread", "That file could not be read: {0}", "Não foi possível ler esse arquivo: {0}", "No se pudo leer ese archivo: {0}");
        A("try.cantplay", "Could not play: {0}", "Não foi possível tocar: {0}", "No se pudo reproducir: {0}");
        A("try.needclip", "This test needs a clip: press Record or open a file first.", "Este teste precisa de um trecho: grave ou abra um arquivo primeiro.", "Esta prueba necesita un fragmento: graba o abre un archivo primero.");
        A("try.needtext", "Type a sentence first.", "Digite uma frase primeiro.", "Escribe una frase primero.");
        A("try.done", "Done in {0} s.", "Concluído em {0} s.", "Terminado en {0} s.");
        A("try.failed", "The test failed: {0}", "O teste falhou: {0}", "La prueba falló: {0}");
        A("try.nothingtosave", "Run a test first.", "Execute um teste primeiro.", "Ejecuta una prueba primero.");
        A("try.saved", "Saved to {0}", "Salvo em {0}", "Guardado en {0}");

        A("try.t.cleanup", "Microphone clean-up: noise, echo, voice lock", "Limpeza do microfone: ruído, eco, trava de voz", "Limpieza del micrófono: ruido, eco, bloqueo de voz");
        A("try.t.noise", "Noise cancellation (Light, Balanced, Max)", "Cancelamento de ruído (Leve, Equilibrado, Máx.)", "Cancelación de ruido (Ligera, Equilibrada, Máx.)");
        A("try.t.transcribe", "Subtitles: live engine vs Whisper", "Legendas: motor ao vivo vs Whisper", "Subtítulos: motor en vivo vs Whisper");
        A("try.t.speakers", "Who is speaking (speaker labels)", "Quem está falando (rótulos de falantes)", "Quién habla (etiquetas de hablantes)");
        A("try.t.translate", "Translation and spoken translation", "Tradução e tradução falada", "Traducción y traducción hablada");
        A("try.t.coach", "English coach", "Treinador de inglês", "Entrenador de inglés");
        A("try.t.assistant", "AI assistant on a sample meeting", "Assistente de IA em uma reunião de exemplo", "Asistente de IA en una reunión de ejemplo");
        A("try.t.encoders", "Video encoders on this PC", "Codificadores de vídeo neste PC", "Codificadores de vídeo en este PC");
        A("try.hint.reference", "Optional: type what you said and the test will count the word errors.", "Opcional: digite o que você disse e o teste contará os erros de palavras.", "Opcional: escribe lo que dijiste y la prueba contará los errores de palabras.");
        A("try.hint.translate", "Type a sentence to translate, or leave empty to translate what you recorded.", "Digite uma frase para traduzir ou deixe vazio para traduzir o que você gravou.", "Escribe una frase para traducir o déjalo vacío para traducir lo que grabaste.");

        A("try.r.original", "Original (as recorded)", "Original (como gravado)", "Original (tal como se grabó)");
        A("try.r.original.details", "Background {0} dB · Voice {1} dB · Clarity (voice above background) {2} dB",
            "Fundo {0} dB · Voz {1} dB · Clareza (voz acima do fundo) {2} dB", "Fondo {0} dB · Voz {1} dB · Claridad (voz sobre el fondo) {2} dB");
        A("try.r.metrics", "Background {0} dB · Your voice {1} dB · Clarity {2} → {3} dB · Uses {4} of real time {5}",
            "Fundo {0} dB · Sua voz {1} dB · Clareza {2} → {3} dB · Usa {4} do tempo real {5}", "Fondo {0} dB · Tu voz {1} dB · Claridad {2} → {3} dB · Usa {4} del tiempo real {5}");
        A("try.r.fast", "✓ comfortable", "✓ tranquilo", "✓ cómodo");
        A("try.r.tight", "⚠ tight", "⚠ no limite", "⚠ justo");
        A("try.r.slow", "✗ too slow for live use", "✗ lento demais para uso ao vivo", "✗ demasiado lento para uso en vivo");
        A("try.r.light", "Noise cancellation: Light", "Cancelamento de ruído: Leve", "Cancelación de ruido: Ligera");
        A("try.r.balanced", "Noise cancellation: Balanced", "Cancelamento de ruído: Equilibrado", "Cancelación de ruido: Equilibrada");
        A("try.r.max", "Noise cancellation: Max", "Cancelamento de ruído: Máximo", "Cancelación de ruido: Máxima");
        A("try.r.echo", "Echo cancellation", "Cancelamento de eco", "Cancelación de eco");
        A("try.r.echo.details", "Echo reduced by {0} dB · delay found {1} ms ({2}) · uses {3} of real time",
            "Eco reduzido em {0} dB · atraso encontrado {1} ms ({2}) · usa {3} do tempo real", "Eco reducido en {0} dB · retardo encontrado {1} ms ({2}) · usa {3} del tiempo real");
        A("try.r.locked", "locked", "travado", "fijado");
        A("try.r.notlocked", "not locked: play something while recording", "não travou: toque algo durante a gravação", "no fijado: reproduce algo mientras grabas");
        A("try.r.noecho", "Not tested. Tick “Also record what the PC plays”, play a video through the speakers and record again.",
            "Não testado. Marque “Gravar também o que o PC toca”, toque um vídeo nos alto-falantes e grave de novo.", "No probado. Marca “Grabar también lo que reproduce el PC”, reproduce un vídeo por los altavoces y graba de nuevo.");
        A("try.r.voicelock", "Voice lock (only your voice)", "Trava de voz (somente a sua voz)", "Bloqueo de voz (solo tu voz)");
        A("try.r.voicelock.balanced", "Voice lock + noise Balanced", "Trava de voz + ruído Equilibrado", "Bloqueo de voz + ruido Equilibrado");
        A("try.r.voicelock.max", "Voice lock + noise Max", "Trava de voz + ruído Máx.", "Bloqueo de voz + ruido Máx.");
        A("try.r.novoicelock", "Not tested: train your voice first (Meetings › Train my voice…).", "Não testado: treine sua voz primeiro (Reuniões › Treinar minha voz…).", "No probado: entrena tu voz primero (Reuniones › Entrenar mi voz…).");
        A("try.r.preparing", "Preparing {0}…", "Preparando {0}…", "Preparando {0}…");
        A("try.r.running", "Running {0}…", "Executando {0}…", "Ejecutando {0}…");
        A("try.r.downloading", "Downloading… {0} (one time)", "Baixando… {0} (uma vez)", "Descargando… {0} (una vez)");
        A("try.r.ai", "the AI model", "o modelo de IA", "el modelo de IA");
        A("try.r.stream", "Live subtitles engine (English)", "Motor de legendas ao vivo (inglês)", "Motor de subtítulos en vivo (inglés)");
        A("try.r.whisper", "Whisper (refined transcript) on the original", "Whisper (transcrição refinada) no original", "Whisper (transcripción refinada) en el original");
        A("try.r.whisper.clean", "Whisper on the clip cleaned with Balanced", "Whisper no trecho limpo com Equilibrado", "Whisper en el fragmento limpiado con Equilibrada");
        A("try.r.text", "“{0}”\nTook {1} s ({2} of the clip length){3}", "“{0}”\nLevou {1} s ({2} da duração do trecho){3}", "“{0}”\nTardó {1} s ({2} de la duración del fragmento){3}");
        A("try.r.wer", " · word errors {0}", " · erros de palavras {0}", " · errores de palabras {0}");
        A("try.r.nospeech", "No speech was found.", "Nenhuma fala foi encontrada.", "No se encontró voz.");
        A("try.r.speaker", "{0} s in {1} turn(s): {2}", "{0} s em {1} turno(s): {2}", "{0} s en {1} turno(s): {2}");
        A("try.r.time", "Time", "Tempo", "Tiempo");
        A("try.r.analysed", "Analysed in {0} s", "Analisado em {0} s", "Analizado en {0} s");
        A("try.r.src", "What was said", "O que foi dito", "Lo que se dijo");
        A("try.r.tr", "Translation to {0}", "Tradução para {0}", "Traducción a {0}");
        A("try.r.ms", "{0} ms", "{0} ms", "{0} ms");
        A("try.r.fallback", "(the local AI was used because the cloud one failed)", "(a IA local foi usada porque a da nuvem falhou)", "(se usó la IA local porque la de la nube falló)");
        A("try.r.spoken", "Spoken translation", "Tradução falada", "Traducción hablada");
        A("try.r.voice", "Voice: {0}", "Voz: {0}", "Voz: {0}");
        A("try.r.novoice", "No Windows voice for {0}. Add one in Windows Settings › Time & language › Speech.", "Sem voz do Windows para {0}. Adicione uma em Configurações › Hora e idioma › Fala.", "Sin voz de Windows para {0}. Añade una en Configuración › Hora e idioma › Voz.");
        A("try.r.you", "Your sentence", "Sua frase", "Tu frase");
        A("try.r.fix", "Corrected", "Corrigida", "Corregida");
        A("try.r.ok", "Nothing to correct. Nice.", "Nada a corrigir. Muito bem.", "Nada que corregir. Muy bien.");
        A("try.r.pace", "Pace: {0} words per minute", "Ritmo: {0} palavras por minuto", "Ritmo: {0} palabras por minuto");
        A("try.r.sample", "Sample meeting", "Reunião de exemplo", "Reunión de ejemplo");
        A("try.r.summary", "Summary", "Resumo", "Resumen");
        A("try.r.actions", "Action items", "Itens de ação", "Tareas pendientes");
        A("try.r.chapters", "Chapters", "Capítulos", "Capítulos");
        A("try.r.chosen", "used by MeetWise", "usado pelo MeetWise", "usado por MeetWise");
        A("try.r.codec", "Codec: {0}", "Codec: {0}", "Códec: {0}");
        A("try.r.noenc", "No hardware encoder was found; x264 on the CPU will be used.", "Nenhum codificador de hardware encontrado; será usado x264 na CPU.", "No se encontró codificador por hardware; se usará x264 en la CPU.");
    }
}
