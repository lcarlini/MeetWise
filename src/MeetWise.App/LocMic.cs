namespace MeetWise.App;

internal static partial class LocTexts
{
    private static void Mic()
    {
        A("app.subtitle", "Clean microphone for Zoom, Teams, Meet and Discord", "Microfone limpo para Zoom, Teams, Meet e Discord");
        A("app.startup", "Start with Windows (the microphone is ready before any meeting app opens)", "Iniciar com o Windows (o microfone fica pronto antes de abrir qualquer app de reunião)");
        A("app.closehint", "Closing this window keeps the microphone working. To stop it completely, right-click the MeetWise icon next to the clock and choose Quit.", "Fechar esta janela mantém o microfone funcionando. Para parar de vez, clique com o botão direito no ícone do MeetWise perto do relógio e escolha Sair.");

        A("mic.turnon", "Turn on", "Ligar");
        A("mic.turnoff", "Turn off", "Desligar");
        A("mic.install", "Create MeetWise Microphone", "Criar Microfone MeetWise");
        A("mic.install.confirm", "MeetWise will download the free VB-CABLE virtual audio driver by VB-Audio (about 1 MB, from vb-audio.com) and install it. Windows asks for administrator permission once, and may need a restart before the device appears.\n\n{0}\n\nContinue?", "O MeetWise vai baixar o driver de áudio virtual gratuito VB-CABLE, da VB-Audio (cerca de 1 MB, de vb-audio.com), e instalá-lo. O Windows pede permissão de administrador uma vez e pode precisar reiniciar antes de o dispositivo aparecer.\n\n{0}\n\nContinuar?");
        A("mic.installing", "Downloading and installing the virtual microphone…", "Baixando e instalando o microfone virtual…");
        A("mic.repair", "Repair", "Reparar");
        A("mic.repairing", "Repairing the virtual microphone (Windows may ask for administrator permission)…", "Reparando o microfone virtual (o Windows pode pedir permissão de administrador)…");
        A("mic.enable.confirm", "MeetWise will switch the MeetWise Microphone back on in Windows (the same as \"Enable\" in the Sound control panel).\n\nOn some PCs with NVIDIA Broadcast running, Windows audio briefly stops responding after a device change. If sound stops, restart the PC: the microphone keeps working afterwards.", "O MeetWise vai reativar o Microfone MeetWise no Windows (o mesmo que \"Habilitar\" no Painel de Som).\n\nEm alguns PCs com o NVIDIA Broadcast aberto, o áudio do Windows para de responder por um tempo depois de uma mudança de dispositivo. Se o som parar, reinicie o PC: depois disso o microfone continua funcionando.");
        A("mic.enabled", "The MeetWise Microphone was disabled in Windows and has been switched back on ({0} device(s)).", "O Microfone MeetWise estava desativado no Windows e foi reativado ({0} dispositivo(s)).");
        A("mic.makedefault", "Make it the Windows default", "Tornar padrão do Windows");
        A("tip.makedefault", "Apps set to \"Same as system\" / \"Default\" then use the clean microphone automatically.", "Apps configurados como \"Igual ao sistema\" / \"Padrão\" passam a usar o microfone limpo automaticamente.");
        A("mic.restore", "Restore my previous default", "Restaurar meu padrão anterior");
        A("mic.howto", "In Zoom, Teams or Meet open the audio settings and choose \"MeetWise Microphone\" as the microphone. Tip: in Zoom set its own background-noise suppression to Low (MeetWise already removed it).", "No Zoom, Teams ou Meet, abra as configurações de áudio e escolha \"MeetWise Microphone\" como microfone. Dica: no Zoom, deixe a supressão de ruído do próprio Zoom em Baixa (o MeetWise já removeu o ruído).");
        A("mic.busy.download", "Downloading the AI model… {0} (one time)", "Baixando o modelo de IA… {0} (só uma vez)");
        A("mic.busy.starting", "Starting the clean microphone…", "Ligando o microfone limpo…");
        A("mic.err.notinstalled", "The MeetWise Microphone is not installed yet.", "O Microfone MeetWise ainda não está instalado.");
        A("mic.default.notrunning", "The clean microphone could not be started, so it was not made the default (apps would hear silence).", "O microfone limpo não pôde ser ligado, então não foi definido como padrão (os apps ouviriam silêncio).");
        A("mic.default.done", "MeetWise Microphone is now the Windows default microphone.", "O Microfone MeetWise agora é o microfone padrão do Windows.");
        A("mic.default.failed", "Windows did not change the default. Choose \"MeetWise Microphone\" in Settings › System › Sound › Input.", "O Windows não mudou o padrão. Escolha \"MeetWise Microphone\" em Configurações › Sistema › Som › Entrada.");
        A("mic.restore.done", "Your previous microphone is the Windows default again.", "Seu microfone anterior voltou a ser o padrão do Windows.");
        A("mic.restore.failed", "Could not restore it. Choose your microphone in Settings › System › Sound › Input.", "Não foi possível restaurar. Escolha seu microfone em Configurações › Sistema › Som › Entrada.");
        A("mic.source", "Your microphone", "Seu microfone");

        A("state.on", "On: Zoom and Teams hear your clean voice", "Ligado: Zoom e Teams ouvem sua voz limpa");
        A("state.on.detail", "From {0}. Applied: {1}.", "De {0}. Aplicado: {1}.");
        A("state.off", "Off", "Desligado");
        A("state.off.detail", "Apps that use the MeetWise Microphone hear silence while it is off.", "Os apps que usam o Microfone MeetWise ouvem silêncio enquanto ele está desligado.");
        A("state.waiting", "Waiting for your microphone…", "Aguardando seu microfone…");
        A("state.notinstalled", "MeetWise Microphone not installed", "Microfone MeetWise não instalado");
        A("state.notinstalled.detail", "Create it once; it then appears in every meeting app.", "Crie uma vez; depois ele aparece em todos os apps de reunião.");
        A("state.disabled", "MeetWise Microphone is disabled in Windows", "O Microfone MeetWise está desativado no Windows");
        A("state.broken", "MeetWise Microphone needs a repair", "O Microfone MeetWise precisa de reparo");
        A("state.broken.detail", "Zoom and Teams cannot use it in this state. Click Repair.", "Zoom e Teams não conseguem usá-lo assim. Clique em Reparar.");
        A("state.nohost", "The background microphone is not running", "O microfone em segundo plano não está rodando");
        A("state.nohost.detail", "Open MeetWise again, or restart Windows.", "Abra o MeetWise de novo ou reinicie o Windows.");
        A("state.isdefault", "It is the Windows default microphone.", "É o microfone padrão do Windows.");
        A("state.defaultsilent", "Warning: it is the Windows default microphone, so apps hear silence while it is off.", "Atenção: ele é o microfone padrão do Windows, então os apps ouvem silêncio enquanto está desligado.");
        A("state.fixed", "Fixed automatically: {0}.", "Corrigido automaticamente: {0}.");

        A("meter.in", "Microphone", "Microfone");
        A("meter.out", "What they hear", "O que eles ouvem");
        A("meter.stats", "Delay {0} ms · processing {1} of real time · auto gain {2} dB", "Atraso {0} ms · processamento {1} do tempo real · ganho automático {2} dB");

        A("fx.title", "Clean-up", "Limpeza");
        A("fx.engine", "Who cleans the microphone", "Quem limpa o microfone");
        A("fx.engine.broadcast", "NVIDIA Broadcast removes noise and room echo: turn on Noise Removal and Room Echo Removal in its app. MeetWise reads \"Microphone (NVIDIA Broadcast)\" and only adds Accent Assist (and speaker-echo cancellation or automatic volume if you switch them on). In Zoom/Teams keep choosing MeetWise Microphone, not the Broadcast one.", "O NVIDIA Broadcast remove ruído e eco da sala: ative Noise Removal e Room Echo Removal no app dele. O MeetWise lê o \"Microphone (NVIDIA Broadcast)\" e só acrescenta o Accent Assist (e o cancelamento de eco do alto-falante ou o volume automático, se você ligar). No Zoom/Teams continue escolhendo MeetWise Microphone, não o do Broadcast.");
        A("fx.engine.notrunning", "The NVIDIA Broadcast app is not running, so its microphone carries no cleaned audio. Open NVIDIA Broadcast; until then MeetWise cleans the microphone itself if Broadcast's microphone fails.", "O app NVIDIA Broadcast não está aberto, então o microfone dele não traz áudio limpo. Abra o NVIDIA Broadcast; até lá, o MeetWise limpa o microfone sozinho se o do Broadcast falhar.");
        A("fx.engine.missing", "No NVIDIA Broadcast microphone was found, so MeetWise cleans the microphone itself.", "Nenhum microfone do NVIDIA Broadcast foi encontrado, então o MeetWise limpa o microfone sozinho.");
        A("fx.engine.nobroadcast", "NVIDIA Broadcast is not installed (no \"Microphone (NVIDIA Broadcast)\").", "O NVIDIA Broadcast não está instalado (não há \"Microphone (NVIDIA Broadcast)\").");
        A("fx.noise", "Remove background noise", "Remover ruído de fundo");
        A("fx.noise.hint", "Dogs, birds, keyboard and mouse clicks, fans, traffic, TV and room noise.", "Latidos, passarinhos, teclado e mouse, ventilador, trânsito, TV e ruído do ambiente.");
        A("fx.strength", "Strength", "Intensidade");
        A("noise.max", "Max (full band, 48 kHz)", "Máxima (banda cheia, 48 kHz)");
        A("noise.balanced", "Balanced (16 kHz)", "Equilibrada (16 kHz)");
        A("noise.light", "Light (any PC)", "Leve (qualquer PC)");
        A("fx.echo", "Remove echo", "Remover eco");
        A("fx.echo.hint", "Removes the other people's voices when they come back through your speakers into your microphone.", "Remove a voz das outras pessoas quando ela volta dos seus alto-falantes para o seu microfone.");
        A("fx.voices", "Remove background voices", "Remover vozes de fundo");
        A("fx.voices.hint", "Keeps only your voice: colleagues, people nearby, shouting and TV dialogue are removed. Uses your trained voice.", "Mantém só a sua voz: colegas, pessoas por perto, gritos e falas da TV são removidos. Usa a sua voz treinada.");
        A("fx.voices.untrained", "Needs about a minute to learn your voice first (you read six short sentences).", "Precisa de cerca de um minuto para aprender sua voz primeiro (você lê seis frases curtas).");
        A("fx.train", "Train my voice", "Treinar minha voz");
        A("fx.gain", "Automatic volume", "Volume automático");
        A("fx.gain.hint", "Keeps your voice at the same level whether you lean in or sit back. It never raises background noise in pauses.", "Mantém sua voz no mesmo nível quer você chegue perto ou se afaste. Nunca aumenta o ruído nas pausas.");

        A("accent.title", "Accent assist (phase 2)", "Assistente de sotaque (fase 2)");
        A("accent.enable", "Accent assist for English (Brazilian speaker → US listeners)", "Assistente de sotaque para inglês (brasileiro → ouvintes dos EUA)");
        A("accent.strength", "Strength", "Intensidade");
        A("accent.hint.ready", "Experimental. Pulls your pronunciation towards native US English in real time (about 0.3 s of delay). Measured on your recordings it still changes your voice and some short words, so keep it off for important calls until it passes the quality checks in docs/QUALITY-REPORT.md.", "Experimental. Aproxima sua pronúncia do inglês nativo dos EUA em tempo real (cerca de 0,3 s de atraso). Medido nas suas gravações, ainda altera sua voz e algumas palavras curtas, então deixe desligado em chamadas importantes até passar nos testes de qualidade em docs/QUALITY-REPORT.md.");
        A("accent.hint.missing", "Not installed: {0} is missing (see docs/PLAN.md, Accent Assist).", "Não instalado: falta {0} (veja docs/PLAN.md, Accent Assist).");
        A("accent.status", "Accent Assist: {0}.", "Accent Assist: {0}.");
        A("accent.hint", "Not installed yet. It will adjust pronunciation and rhythm slightly in real time, keeping your own voice, with a small delay.", "Ainda não instalado. Vai ajustar levemente a pronúncia e o ritmo em tempo real, mantendo a sua voz, com um pequeno atraso.");

        A("test.title", "Test", "Teste");
        A("test.hint", "Hear yourself exactly as Zoom/Teams hear you: talk for 6 seconds (try typing or playing a video at the same time), then play both versions.", "Ouça-se exatamente como o Zoom/Teams te ouvem: fale por 6 segundos (tente digitar ou tocar um vídeo ao mesmo tempo) e depois ouça as duas versões.");
        A("test.zoom", "Test as Zoom hears me", "Testar como o Zoom me ouve");
        A("test.playraw", "Play original", "Ouvir original");
        A("test.playclean", "Play what they hear", "Ouvir o que eles ouvem");
        A("test.check", "Check connection", "Verificar conexão");
        A("test.speak", "Recording 6 seconds from the MeetWise Microphone… speak now.", "Gravando 6 segundos do Microfone MeetWise… fale agora.");
        A("test.silent", "The MeetWise Microphone was silent. Click \"Check connection\" or Repair.", "O Microfone MeetWise ficou em silêncio. Clique em \"Verificar conexão\" ou em Reparar.");
        A("test.done", "Recorded from the MeetWise Microphone: voice {0} dBFS, background {1} dBFS.", "Gravado do Microfone MeetWise: voz {0} dBFS, fundo {1} dBFS.");
        A("test.compare", "Background noise removed: {0} dB compared with your raw microphone.", "Ruído de fundo removido: {0} dB em relação ao microfone sem tratamento.");
        A("test.checking", "Sending a test tone through the virtual microphone…", "Enviando um tom de teste pelo microfone virtual…");

        A("tray.open", "Open MeetWise", "Abrir MeetWise");
        A("tray.on", "Clean microphone on", "Microfone limpo ligado");
        A("tray.quit", "Quit (the microphone stops)", "Sair (o microfone para)");
        A("tray.tip.on", "MeetWise: clean microphone on", "MeetWise: microfone limpo ligado");
        A("tray.tip.off", "MeetWise: clean microphone off", "MeetWise: microfone limpo desligado");
        A("tray.tip.problem", "MeetWise: the microphone needs attention", "MeetWise: o microfone precisa de atenção");
        A("quit.default", "MeetWise Microphone is your Windows default microphone. If MeetWise quits, apps will hear silence.\n\nSwitch back to your previous microphone now?", "O Microfone MeetWise é seu microfone padrão do Windows. Se o MeetWise sair, os apps vão ouvir silêncio.\n\nVoltar para o microfone anterior agora?");
    }
}
