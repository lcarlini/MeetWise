using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Markup;

namespace MeetWise.App;

/// <summary>
/// Interface texts of the Studio pages in English, Portuguese and Spanish (the three languages of the subtitle extractor). XAML uses
/// <c>{local:T key}</c>; changing <see cref="Language"/> updates every visible text immediately.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    public static Loc Instance { get; } = new();

    private string _language = "en";

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Language
    {
        get => _language;
        set
        {
            var v = Texts.ContainsKey(value) ? value : "en";
            if (v == _language) return;
            _language = v;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]")); // every binding to the indexer refreshes
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
        }
    }

    public string this[string key] => Get(key, _language);

    public static string T(string key, params object[] args)
    {
        var text = Instance[key];
        return args.Length == 0 ? text : string.Format(CultureInfo.CurrentCulture, text, args);
    }

    public static string Get(string key, string language)
        => Texts.TryGetValue(language, out var d) && d.TryGetValue(key, out var v) ? v
         : Texts["en"].TryGetValue(key, out var en) ? en : key;

    public static IEnumerable<string> Keys => Texts["en"].Keys;
    public static IReadOnlyList<string> SupportedLanguages { get; } = new[] { "en", "pt", "es" };

    public static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Texts = new Dictionary<string, IReadOnlyDictionary<string, string>>
    {
        ["en"] = new Dictionary<string, string>
        {
            ["nav.record"] = "Record", ["nav.subtitles"] = "Subtitles", ["nav.assistant"] = "AI Assistant", ["nav.library"] = "Library",
            ["nav.meetings"] = "Meetings", ["nav.settings"] = "Settings",

            ["rec.title"] = "Record your screen with live subtitles", ["rec.monitor"] = "Screen", ["rec.mic"] = "Microphone", ["rec.sysaudio"] = "System audio",
            ["rec.usemic"] = "Record microphone", ["rec.mode"] = "What to save", ["rec.mode.video"] = "Video + audio", ["rec.mode.audio"] = "Audio only",
            ["rec.mode.text"] = "Subtitles only", ["rec.quality"] = "Quality", ["rec.q.low"] = "Low file size", ["rec.q.balanced"] = "Balanced",
            ["rec.q.max"] = "Maximum quality", ["rec.language"] = "Spoken language", ["rec.english"] = "Subtitles in English", ["rec.srt"] = "Save .srt file",
            ["rec.floating"] = "Floating captions", ["rec.start"] = "Start recording", ["rec.stop"] = "Stop", ["rec.live"] = "Live subtitles",
            ["rec.speaking"] = "listening…", ["rec.waiting"] = "Subtitles appear here while you record.", ["rec.saved"] = "Saved: {0}",
            ["rec.open"] = "Open subtitles", ["rec.openfolder"] = "Open folder", ["rec.play"] = "Play video", ["rec.nosource"] = "Choose at least one audio source.",
            ["rec.preparing"] = "Preparing…", ["rec.finishing"] = "Finishing the video and subtitles…", ["rec.hotkey"] = "Ctrl+Shift+R starts and stops from anywhere",

            ["sub.title"] = "Subtitles", ["sub.item"] = "Recording", ["sub.selectall"] = "All", ["sub.selectnone"] = "None", ["sub.invert"] = "Invert",
            ["sub.filter"] = "Filter…", ["sub.copytext"] = "Copy text", ["sub.copysrt"] = "Copy as SRT", ["sub.savesrt"] = "Save SRT", ["sub.savevtt"] = "Save VTT",
            ["sub.savetxt"] = "Save TXT", ["sub.ask"] = "Ask AI about selection", ["sub.refine"] = "Refine with Whisper", ["sub.openvideo"] = "Open video",
            ["sub.selected"] = "{0} of {1} selected", ["sub.empty"] = "No subtitles yet. Record something or pick a recording above.", ["sub.copied"] = "Copied {0} line(s).",
            ["sub.saved"] = "Saved {0}", ["sub.col.time"] = "Time", ["sub.col.speaker"] = "Speaker", ["sub.col.text"] = "Text", ["sub.refining"] = "Refining…",

            ["ai.title"] = "AI Assistant", ["ai.context.all"] = "Working on the whole recording ({0} lines)", ["ai.context.sel"] = "Working on the {0} selected line(s)",
            ["ai.send"] = "Send", ["ai.placeholder"] = "Ask anything about this recording, or give a command…", ["ai.clear"] = "Clear chat", ["ai.copy"] = "Copy",
            ["ai.save"] = "Save as file", ["ai.cmd.summarize"] = "Summarize", ["ai.cmd.keypoints"] = "Key points", ["ai.cmd.actions"] = "Action items",
            ["ai.cmd.chapters"] = "Chapters", ["ai.cmd.title"] = "Title & description", ["ai.cmd.email"] = "Follow-up email", ["ai.cmd.cleanup"] = "Clean up text",
            ["ai.cmd.translate"] = "Translate to", ["ai.thinking"] = "Thinking…", ["ai.empty"] = "Pick a command or type a question. The assistant only uses what was said in the recording.",
            ["ai.engine"] = "Engine: {0}", ["ai.you"] = "You", ["ai.assistant"] = "Assistant", ["ai.stop"] = "Stop", ["ai.saved"] = "Saved {0}",

            ["lib.title"] = "Library", ["lib.search"] = "Search all transcripts…", ["lib.import"] = "Import audio/video…", ["lib.youtube"] = "Import YouTube",
            ["lib.youtube.hint"] = "Paste a YouTube link", ["lib.watch"] = "Watch a folder", ["lib.watch.folder"] = "Folder to watch", ["lib.open.sub"] = "Subtitles",
            ["lib.open.ai"] = "Ask AI", ["lib.folder"] = "Folder", ["lib.delete"] = "Delete", ["lib.rename"] = "Rename", ["lib.kind.recording"] = "Recording",
            ["lib.kind.import"] = "Imported", ["lib.kind.youtube"] = "YouTube", ["lib.kind.meeting"] = "Meeting", ["lib.confirmdelete"] = "Delete \"{0}\" and its subtitles? The files on disk are kept.",
            ["lib.jobs"] = "Imports", ["lib.opt.language"] = "Language of the audio", ["lib.opt.translate"] = "Translate to English", ["lib.opt.noise"] = "Reduce noise",
            ["lib.opt.srt"] = "Also save .srt", ["lib.empty"] = "Nothing here yet. Record something or import a file.", ["lib.queued"] = "Waiting", ["lib.working"] = "Working",
            ["lib.done"] = "Done", ["lib.failed"] = "Failed",

            ["set.language"] = "Interface language", ["common.close"] = "Close"
        },
        ["pt"] = new Dictionary<string, string>
        {
            ["nav.record"] = "Gravar", ["nav.subtitles"] = "Legendas", ["nav.assistant"] = "Assistente de IA", ["nav.library"] = "Biblioteca",
            ["nav.meetings"] = "Reuniões", ["nav.settings"] = "Configurações",

            ["rec.title"] = "Grave sua tela com legendas ao vivo", ["rec.monitor"] = "Tela", ["rec.mic"] = "Microfone", ["rec.sysaudio"] = "Áudio do sistema",
            ["rec.usemic"] = "Gravar microfone", ["rec.mode"] = "O que salvar", ["rec.mode.video"] = "Vídeo + áudio", ["rec.mode.audio"] = "Somente áudio",
            ["rec.mode.text"] = "Somente legendas", ["rec.quality"] = "Qualidade", ["rec.q.low"] = "Arquivo menor", ["rec.q.balanced"] = "Equilibrada",
            ["rec.q.max"] = "Qualidade máxima", ["rec.language"] = "Idioma falado", ["rec.english"] = "Legendas em inglês", ["rec.srt"] = "Salvar arquivo .srt",
            ["rec.floating"] = "Legendas flutuantes", ["rec.start"] = "Iniciar gravação", ["rec.stop"] = "Parar", ["rec.live"] = "Legendas ao vivo",
            ["rec.speaking"] = "ouvindo…", ["rec.waiting"] = "As legendas aparecem aqui enquanto você grava.", ["rec.saved"] = "Salvo: {0}",
            ["rec.open"] = "Abrir legendas", ["rec.openfolder"] = "Abrir pasta", ["rec.play"] = "Reproduzir vídeo", ["rec.nosource"] = "Escolha pelo menos uma fonte de áudio.",
            ["rec.preparing"] = "Preparando…", ["rec.finishing"] = "Finalizando o vídeo e as legendas…", ["rec.hotkey"] = "Ctrl+Shift+R inicia e para de qualquer lugar",

            ["sub.title"] = "Legendas", ["sub.item"] = "Gravação", ["sub.selectall"] = "Todas", ["sub.selectnone"] = "Nenhuma", ["sub.invert"] = "Inverter",
            ["sub.filter"] = "Filtrar…", ["sub.copytext"] = "Copiar texto", ["sub.copysrt"] = "Copiar como SRT", ["sub.savesrt"] = "Salvar SRT", ["sub.savevtt"] = "Salvar VTT",
            ["sub.savetxt"] = "Salvar TXT", ["sub.ask"] = "Perguntar à IA sobre a seleção", ["sub.refine"] = "Refinar com Whisper", ["sub.openvideo"] = "Abrir vídeo",
            ["sub.selected"] = "{0} de {1} selecionadas", ["sub.empty"] = "Ainda não há legendas. Grave algo ou escolha uma gravação acima.", ["sub.copied"] = "{0} linha(s) copiada(s).",
            ["sub.saved"] = "Salvo {0}", ["sub.col.time"] = "Tempo", ["sub.col.speaker"] = "Falante", ["sub.col.text"] = "Texto", ["sub.refining"] = "Refinando…",

            ["ai.title"] = "Assistente de IA", ["ai.context.all"] = "Trabalhando na gravação inteira ({0} linhas)", ["ai.context.sel"] = "Trabalhando nas {0} linha(s) selecionada(s)",
            ["ai.send"] = "Enviar", ["ai.placeholder"] = "Pergunte qualquer coisa sobre esta gravação ou dê um comando…", ["ai.clear"] = "Limpar conversa", ["ai.copy"] = "Copiar",
            ["ai.save"] = "Salvar como arquivo", ["ai.cmd.summarize"] = "Resumir", ["ai.cmd.keypoints"] = "Pontos principais", ["ai.cmd.actions"] = "Itens de ação",
            ["ai.cmd.chapters"] = "Capítulos", ["ai.cmd.title"] = "Título e descrição", ["ai.cmd.email"] = "E-mail de acompanhamento", ["ai.cmd.cleanup"] = "Limpar texto",
            ["ai.cmd.translate"] = "Traduzir para", ["ai.thinking"] = "Pensando…", ["ai.empty"] = "Escolha um comando ou digite uma pergunta. O assistente usa apenas o que foi dito na gravação.",
            ["ai.engine"] = "Motor: {0}", ["ai.you"] = "Você", ["ai.assistant"] = "Assistente", ["ai.stop"] = "Parar", ["ai.saved"] = "Salvo {0}",

            ["lib.title"] = "Biblioteca", ["lib.search"] = "Pesquisar em todas as transcrições…", ["lib.import"] = "Importar áudio/vídeo…", ["lib.youtube"] = "Importar do YouTube",
            ["lib.youtube.hint"] = "Cole um link do YouTube", ["lib.watch"] = "Monitorar uma pasta", ["lib.watch.folder"] = "Pasta monitorada", ["lib.open.sub"] = "Legendas",
            ["lib.open.ai"] = "Perguntar à IA", ["lib.folder"] = "Pasta", ["lib.delete"] = "Excluir", ["lib.rename"] = "Renomear", ["lib.kind.recording"] = "Gravação",
            ["lib.kind.import"] = "Importado", ["lib.kind.youtube"] = "YouTube", ["lib.kind.meeting"] = "Reunião", ["lib.confirmdelete"] = "Excluir \"{0}\" e suas legendas? Os arquivos no disco são mantidos.",
            ["lib.jobs"] = "Importações", ["lib.opt.language"] = "Idioma do áudio", ["lib.opt.translate"] = "Traduzir para inglês", ["lib.opt.noise"] = "Reduzir ruído",
            ["lib.opt.srt"] = "Salvar também .srt", ["lib.empty"] = "Nada aqui ainda. Grave algo ou importe um arquivo.", ["lib.queued"] = "Na fila", ["lib.working"] = "Processando",
            ["lib.done"] = "Concluído", ["lib.failed"] = "Falhou",

            ["set.language"] = "Idioma da interface", ["common.close"] = "Fechar"
        },
        ["es"] = new Dictionary<string, string>
        {
            ["nav.record"] = "Grabar", ["nav.subtitles"] = "Subtítulos", ["nav.assistant"] = "Asistente de IA", ["nav.library"] = "Biblioteca",
            ["nav.meetings"] = "Reuniones", ["nav.settings"] = "Configuración",

            ["rec.title"] = "Graba tu pantalla con subtítulos en vivo", ["rec.monitor"] = "Pantalla", ["rec.mic"] = "Micrófono", ["rec.sysaudio"] = "Audio del sistema",
            ["rec.usemic"] = "Grabar micrófono", ["rec.mode"] = "Qué guardar", ["rec.mode.video"] = "Vídeo + audio", ["rec.mode.audio"] = "Solo audio",
            ["rec.mode.text"] = "Solo subtítulos", ["rec.quality"] = "Calidad", ["rec.q.low"] = "Archivo pequeño", ["rec.q.balanced"] = "Equilibrada",
            ["rec.q.max"] = "Calidad máxima", ["rec.language"] = "Idioma hablado", ["rec.english"] = "Subtítulos en inglés", ["rec.srt"] = "Guardar archivo .srt",
            ["rec.floating"] = "Subtítulos flotantes", ["rec.start"] = "Iniciar grabación", ["rec.stop"] = "Detener", ["rec.live"] = "Subtítulos en vivo",
            ["rec.speaking"] = "escuchando…", ["rec.waiting"] = "Los subtítulos aparecen aquí mientras grabas.", ["rec.saved"] = "Guardado: {0}",
            ["rec.open"] = "Abrir subtítulos", ["rec.openfolder"] = "Abrir carpeta", ["rec.play"] = "Reproducir vídeo", ["rec.nosource"] = "Elige al menos una fuente de audio.",
            ["rec.preparing"] = "Preparando…", ["rec.finishing"] = "Terminando el vídeo y los subtítulos…", ["rec.hotkey"] = "Ctrl+Shift+R inicia y detiene desde cualquier lugar",

            ["sub.title"] = "Subtítulos", ["sub.item"] = "Grabación", ["sub.selectall"] = "Todas", ["sub.selectnone"] = "Ninguna", ["sub.invert"] = "Invertir",
            ["sub.filter"] = "Filtrar…", ["sub.copytext"] = "Copiar texto", ["sub.copysrt"] = "Copiar como SRT", ["sub.savesrt"] = "Guardar SRT", ["sub.savevtt"] = "Guardar VTT",
            ["sub.savetxt"] = "Guardar TXT", ["sub.ask"] = "Preguntar a la IA sobre la selección", ["sub.refine"] = "Refinar con Whisper", ["sub.openvideo"] = "Abrir vídeo",
            ["sub.selected"] = "{0} de {1} seleccionadas", ["sub.empty"] = "Aún no hay subtítulos. Graba algo o elige una grabación arriba.", ["sub.copied"] = "{0} línea(s) copiada(s).",
            ["sub.saved"] = "Guardado {0}", ["sub.col.time"] = "Tiempo", ["sub.col.speaker"] = "Hablante", ["sub.col.text"] = "Texto", ["sub.refining"] = "Refinando…",

            ["ai.title"] = "Asistente de IA", ["ai.context.all"] = "Trabajando con toda la grabación ({0} líneas)", ["ai.context.sel"] = "Trabajando con las {0} línea(s) seleccionada(s)",
            ["ai.send"] = "Enviar", ["ai.placeholder"] = "Pregunta lo que quieras sobre esta grabación o da una orden…", ["ai.clear"] = "Borrar conversación", ["ai.copy"] = "Copiar",
            ["ai.save"] = "Guardar como archivo", ["ai.cmd.summarize"] = "Resumir", ["ai.cmd.keypoints"] = "Puntos clave", ["ai.cmd.actions"] = "Tareas",
            ["ai.cmd.chapters"] = "Capítulos", ["ai.cmd.title"] = "Título y descripción", ["ai.cmd.email"] = "Correo de seguimiento", ["ai.cmd.cleanup"] = "Limpiar texto",
            ["ai.cmd.translate"] = "Traducir a", ["ai.thinking"] = "Pensando…", ["ai.empty"] = "Elige un comando o escribe una pregunta. El asistente solo usa lo que se dijo en la grabación.",
            ["ai.engine"] = "Motor: {0}", ["ai.you"] = "Tú", ["ai.assistant"] = "Asistente", ["ai.stop"] = "Detener", ["ai.saved"] = "Guardado {0}",

            ["lib.title"] = "Biblioteca", ["lib.search"] = "Buscar en todas las transcripciones…", ["lib.import"] = "Importar audio/vídeo…", ["lib.youtube"] = "Importar de YouTube",
            ["lib.youtube.hint"] = "Pega un enlace de YouTube", ["lib.watch"] = "Vigilar una carpeta", ["lib.watch.folder"] = "Carpeta vigilada", ["lib.open.sub"] = "Subtítulos",
            ["lib.open.ai"] = "Preguntar a la IA", ["lib.folder"] = "Carpeta", ["lib.delete"] = "Eliminar", ["lib.rename"] = "Renombrar", ["lib.kind.recording"] = "Grabación",
            ["lib.kind.import"] = "Importado", ["lib.kind.youtube"] = "YouTube", ["lib.kind.meeting"] = "Reunión", ["lib.confirmdelete"] = "¿Eliminar \"{0}\" y sus subtítulos? Los archivos del disco se conservan.",
            ["lib.jobs"] = "Importaciones", ["lib.opt.language"] = "Idioma del audio", ["lib.opt.translate"] = "Traducir al inglés", ["lib.opt.noise"] = "Reducir ruido",
            ["lib.opt.srt"] = "Guardar también .srt", ["lib.empty"] = "Nada aquí todavía. Graba algo o importa un archivo.", ["lib.queued"] = "En cola", ["lib.working"] = "Procesando",
            ["lib.done"] = "Listo", ["lib.failed"] = "Falló",

            ["set.language"] = "Idioma de la interfaz", ["common.close"] = "Cerrar"
        }
    };
}

/// <summary>XAML: <c>Text="{local:T rec.start}"</c>. Binds to <see cref="Loc"/> so a language change updates the text live.</summary>
public sealed class TExtension : MarkupExtension
{
    public string Key { get; set; } = "";

    public TExtension() { }
    public TExtension(string key) => Key = key;

    public override object ProvideValue(IServiceProvider serviceProvider)
        => new Binding($"[{Key}]") { Source = Loc.Instance, Mode = BindingMode.OneWay }.ProvideValue(serviceProvider);
}
