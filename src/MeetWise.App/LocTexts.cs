namespace MeetWise.App;

/// <summary>Interface texts (English and Brazilian Portuguese; Spanish falls back to English). Each line carries every language together.</summary>
internal static partial class LocTexts
{
    public static readonly List<(string Key, string En, string Pt, string Es)> Items = new();

    private static void A(string key, string en, string pt, string es) => Items.Add((key, en, pt, es));
    private static void A(string key, string en, string pt) => Items.Add((key, en, pt, en));

    static LocTexts()
    {
        Voice();
        Mic();
    }

    private static void Voice()
    {
        A("voice.title", "Train your voice", "Treine sua voz", "Entrena tu voz");
        A("voice.intro", "MeetWise learns your voice from six short sentences, so it can pass only your voice and filter out everyone else. Sit where you normally take calls, in a quiet room, and speak naturally. Only you should speak while training.", "O MeetWise aprende sua voz com seis frases curtas, para deixar passar só a sua voz e filtrar todas as outras. Sente onde você costuma atender chamadas, em um ambiente silencioso, e fale naturalmente. Só você deve falar durante o treino.", "MeetWise aprende tu voz con seis frases cortas, para dejar pasar solo tu voz y filtrar a los demás. Siéntate donde sueles atender llamadas, en un lugar tranquilo, y habla con naturalidad. Solo tú debes hablar durante el entrenamiento.");
        A("voice.start", "Start recording", "Iniciar gravação", "Iniciar grabación");
        A("voice.testtitle", "Test your voice lock", "Teste sua trava de voz", "Prueba tu bloqueo de voz");
        A("voice.testintro", "Record 10 seconds while you talk and someone else (or a video) talks too. Use headphones so the recording does not pick up the playback.", "Grave 10 segundos enquanto você fala e outra pessoa (ou um vídeo) também fala. Use fones para a gravação não captar o som reproduzido.", "Graba 10 segundos mientras hablas y otra persona (o un vídeo) también habla. Usa auriculares para que la grabación no capte la reproducción.");
        A("voice.testrec", "Record 10 s test", "Gravar teste de 10 s", "Grabar prueba de 10 s");
        A("voice.playraw", "Play original", "Ouvir original", "Escuchar original");
        A("voice.playlocked", "Play voice-locked", "Ouvir com trava de voz", "Escuchar con bloqueo de voz");
        A("voice.delete", "Delete my voice profile", "Apagar meu perfil de voz", "Borrar mi perfil de voz");
        A("voice.close", "Close", "Fechar", "Cerrar");
        A("voice.stop", "Stop", "Parar", "Detener");
        A("voice.step", "Sentence {0} of {1}", "Frase {0} de {1}", "Frase {0} de {1}");
        A("tip.voice.record", "Press, read the sentence aloud, then press again.", "Pressione, leia a frase em voz alta e pressione de novo.", "Pulsa, lee la frase en voz alta y vuelve a pulsar.");
        A("tip.voice.level", "Your microphone level while recording.", "O nível do seu microfone durante a gravação.", "El nivel de tu micrófono mientras grabas.");
        A("tip.voice.test", "Record 10 seconds while someone else talks too, then compare.", "Grave 10 segundos enquanto outra pessoa também fala e compare.", "Graba 10 segundos mientras otra persona también habla y compara.");
        A("tip.voice.playraw", "Listen to the test as recorded.", "Ouça o teste como foi gravado.", "Escucha la prueba tal como se grabó.");
        A("tip.voice.playlocked", "Listen to the test with voice lock applied: only your voice should remain.", "Ouça o teste com a trava de voz: só a sua voz deve ficar.", "Escucha la prueba con el bloqueo de voz: solo debería quedar tu voz.");
        A("tip.voice.delete", "Delete your voice profile and turn voice lock off.", "Apaga seu perfil de voz e desliga a trava de voz.", "Borra tu perfil de voz y desactiva el bloqueo de voz.");
        A("tip.voice.close", "Close this window.", "Fecha esta janela.", "Cierra esta ventana.");
    }
}
