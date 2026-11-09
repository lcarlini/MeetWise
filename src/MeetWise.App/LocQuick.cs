namespace MeetWise.App;

/// <summary>Texts of the "Test with my settings" boxes (one per page) and of the applied-settings line.</summary>
internal static partial class LocExtra
{
    private static void QuickTexts()
    {
        A("quick.record", "● Record {0} s", "● Gravar {0} s", "● Grabar {0} s");
        A("quick.file", "Open a file…", "Abrir um arquivo…", "Abrir un archivo…");
        A("tip.quick.record", "Records your microphone, then applies the settings shown on this page. Press again to stop early.", "Grava seu microfone e aplica as configurações mostradas nesta página. Pressione de novo para parar antes.", "Graba tu micrófono y aplica los ajustes de esta página. Pulsa otra vez para parar antes.");
        A("tip.quick.file", "Use an audio or video file (the first 30 seconds).", "Use um arquivo de áudio ou vídeo (os primeiros 30 segundos).", "Usa un archivo de audio o vídeo (los primeros 30 segundos).");
        A("tip.quick.run", "Run the test with the settings shown on this page.", "Executa o teste com as configurações desta página.", "Ejecuta la prueba con los ajustes de esta página.");

        A("noise.off", "Off", "Desligado", "Desactivado");
        A("noise.light", "Light", "Leve", "Ligera");
        A("noise.balanced", "Balanced", "Equilibrado", "Equilibrada");
        A("noise.max", "Max", "Máx.", "Máx.");
        A("cmp.noise", "noise {0}", "ruído {0}", "ruido {0}");
        A("cmp.echo", "echo cancellation", "cancelamento de eco", "cancelación de eco");
        A("cmp.nothing", "nothing (all clean-up is off)", "nada (toda a limpeza está desligada)", "nada (toda la limpieza está desactivada)");
        A("cmp.applied", "Applied: {0}", "Aplicado: {0}", "Aplicado: {0}");
        A("cmp.echo.idle", "Echo cancellation had nothing to remove: nothing was playing on the speakers while you recorded.", "O cancelamento de eco não tinha o que remover: nada tocava nos alto-falantes durante a gravação.", "La cancelación de eco no tenía nada que quitar: no sonaba nada por los altavoces mientras grababas.");
        A("cmp.voicelock.note", "Voice lock is on. On some voices it makes words less clear: if “With my settings” sounds worse, untick “Voice lock” or train your voice again.", "A trava de voz está ligada. Em algumas vozes ela deixa as palavras menos claras: se “Com minhas configurações” soar pior, desmarque “Trava de voz” ou treine sua voz de novo.", "El bloqueo de voz está activado. En algunas voces hace las palabras menos claras: si “Con mis ajustes” suena peor, desmarca “Bloqueo de voz” o entrena tu voz de nuevo.");

        A("quick.coach.title", "Test the English coach", "Teste o treinador de inglês", "Prueba el entrenador de inglés");
        A("quick.coach.hint", "Type an English sentence and press Check, or record yourself saying it.", "Digite uma frase em inglês e pressione Verificar, ou grave você dizendo a frase.", "Escribe una frase en inglés y pulsa Revisar, o grábate diciéndola.");
        A("quick.coach.run", "Check sentence", "Verificar frase", "Revisar frase");
        A("quick.coach.done", "The coach's corrections are below.", "As correções do treinador estão abaixo.", "Las correcciones del entrenador están abajo.");

        A("quick.rec.title", "Test before you record", "Teste antes de gravar", "Prueba antes de grabar");
        A("quick.rec.hint", "Records 10 seconds from the same microphone the recording uses, with the language settings above. Play it back and read the subtitles it would get.", "Grava 10 segundos do mesmo microfone que a gravação usa, com as configurações de idioma acima. Ouça e leia as legendas que ela teria.", "Graba 10 segundos del mismo micrófono que usa la grabación, con los ajustes de idioma de arriba. Escúchalo y lee los subtítulos que tendría.");
        A("quick.rec.cleaned", "Your voice is recorded cleaned by the MeetWise Microphone ({0}). ▶ Original plays exactly what the recording will contain.", "Sua voz é gravada já limpa pelo Microfone MeetWise ({0}). ▶ Original toca exatamente o que a gravação terá.", "Tu voz se graba ya limpia por el Micrófono MeetWise ({0}). ▶ Original reproduce exactamente lo que tendrá la grabación.");
        A("quick.rec.raw", "Your voice is recorded raw (no noise cancellation{0}). Turn on “Always on” in Meetings › MeetWise Microphone to record a cleaned voice.", "Sua voz é gravada sem limpeza (sem cancelamento de ruído{0}). Ative “Sempre ligado” em Reuniões › Microfone MeetWise para gravar a voz limpa.", "Tu voz se graba sin limpiar (sin cancelación de ruido{0}). Activa “Siempre activo” en Reuniones › Micrófono MeetWise para grabar la voz limpia.");
        A("quick.rec.subs", "Subtitles ({0}{1}), made in {2} s:", "Legendas ({0}{1}), feitas em {2} s:", "Subtítulos ({0}{1}), hechos en {2} s:");
        A("quick.rec.english", ", in English", ", em inglês", ", en inglés");
        A("rec.clean.on", "✓ Recorded cleaned: {0}", "✓ Gravado limpo: {0}", "✓ Se graba limpio: {0}");
        A("rec.clean.off", "Recorded raw: no noise cancellation. Turn on the MeetWise Microphone (Meetings) to clean it.", "Gravado sem limpeza: sem cancelamento de ruído. Ligue o Microfone MeetWise (Reuniões) para limpar.", "Se graba sin limpiar: sin cancelación de ruido. Activa el Micrófono MeetWise (Reuniones) para limpiarlo.");

        A("quick.imp.title", "Preview an import", "Prévia de uma importação", "Vista previa de una importación");
        A("quick.imp.hint", "Open a file to hear its first 30 seconds with the options above (Noise filter) and read the subtitles it would get.", "Abra um arquivo para ouvir os primeiros 30 segundos com as opções acima (Filtro de ruído) e ler as legendas que ele teria.", "Abre un archivo para escuchar sus primeros 30 segundos con las opciones de arriba (Filtro de ruido) y leer los subtítulos que tendría.");
        A("quick.imp.summary", "{0}: first {1} s.", "{0}: primeiros {1} s.", "{0}: primeros {1} s.");
        A("quick.imp.filter", "Noise filter is on: ▶ With my settings plays it filtered, and the subtitles below come from the filtered audio.", "Filtro de ruído ligado: ▶ Com minhas configurações toca o áudio filtrado, e as legendas abaixo vêm dele.", "Filtro de ruido activado: ▶ Con mis ajustes reproduce el audio filtrado, y los subtítulos de abajo salen de él.");
        A("quick.imp.nofilter", "Noise filter is off, so the file is used as it is. Tick “Noise filter” and preview again to compare.", "Filtro de ruído desligado, então o arquivo é usado como está. Marque “Filtro de ruído” e veja de novo para comparar.", "Filtro de ruido desactivado, así que el archivo se usa tal cual. Marca “Filtro de ruido” y vuelve a probar para comparar.");
        A("quick.imp.change", "Background {0} dB quieter, voice {1} dB.", "Fundo {0} dB mais baixo, voz {1} dB.", "Fondo {0} dB más bajo, voz {1} dB.");

        A("quick.ai.title", "Try the AI on a sample meeting", "Teste a IA em uma reunião de exemplo", "Prueba la IA en una reunión de ejemplo");
        A("quick.ai.hint", "Runs a summary, the action items and chapters on a short built-in meeting, with the AI engine chosen in Settings, and shows how long each took.", "Gera resumo, itens de ação e capítulos de uma reunião curta embutida, com o motor de IA escolhido nas Configurações, e mostra quanto tempo cada um levou.", "Hace un resumen, las tareas y capítulos de una reunión corta incluida, con el motor de IA elegido en Ajustes, y muestra cuánto tardó cada uno.");
        A("quick.ai.run", "Try on sample meeting", "Testar na reunião de exemplo", "Probar en la reunión de ejemplo");
        A("quick.ai.done", "Done. The answers are below.", "Pronto. As respostas estão abaixo.", "Listo. Las respuestas están abajo.");

        A("quick.tr.title", "Test live translation", "Teste a tradução ao vivo", "Prueba la traducción en vivo");
        A("quick.tr.hint", "Type a sentence and press Translate: it uses the caption language chosen above and plays it with a Windows voice.", "Digite uma frase e pressione Traduzir: usa o idioma de legenda escolhido acima e toca com uma voz do Windows.", "Escribe una frase y pulsa Traducir: usa el idioma de subtítulos elegido arriba y la reproduce con una voz de Windows.");
        A("quick.tr.run", "Translate", "Traduzir", "Traducir");
        A("quick.tr.done", "Translated. ▶ With my settings plays the spoken translation.", "Traduzido. ▶ Com minhas configurações toca a tradução falada.", "Traducido. ▶ Con mis ajustes reproduce la traducción hablada.");
    }
}
