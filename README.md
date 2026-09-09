# 📱 TwitchPhoneMod — GTA V Twitch Phone Calls

Mod para **GTA V Enhanced** (Modo História/PC) que transforma mensagens do chat da Twitch em **ligações telefônicas dentro do jogo**: o personagem atende, leva o celular ao ouvido, e a mensagem é reproduzida em voz sintética com legenda na tela.

> Este repositório é a metade **C#** do projeto — roda dentro do GTA V via ScriptHookVDotNet. A outra metade (que escuta a Twitch e gera a voz) fica em [`gtaV-twitch-calls-integration`](#) *(https://github.com/leviberga/GtaV-TwitchChat-Integration)*.

---

## ✨ Funcionalidades

- HUD customizada de smartphone, desenhada do zero (sem depender de hashes instáveis da Rockstar)
- Modelo 3D real do celular (`p_amb_phone_01`), anexado na mão do personagem
- Animação de atender a ligação, com o braço travado na orelha durante toda a fala
- Legendas sincronizadas com o áudio
- Fila de ligações — nunca sobrepõe áudio, mesmo com vários espectadores chamando ao mesmo tempo
- Comunicação em tempo real com o bridge Java via WebSocket

---

## 📦 Requisitos

- **GTA V Enhanced** (`GTA5_Enhanced.exe`)
- **[Script Hook V .Net Enhanced](https://github.com/Chiheb-Bacha/ScriptHookVDotNetEnhanced)** — build específico com suporte ao Enhanced (o SHVDN "padrão" tem problemas conhecidos de compatibilidade nessa versão do jogo, ver seção de troubleshooting abaixo)
- .NET Framework 4.8
- [NAudio](https://github.com/naudio/NAudio) 2.1.0
- [Fleck](https://github.com/statianzo/Fleck) (servidor WebSocket embutido)
- **[Kokoro-TTS](https://github.com/remsky/Kokoro-FastAPI)** rodando localmente (via o [bridge Java](#))

---

## ⚙️ Instalação

1. Instale o **Script Hook V .Net Enhanced** na raiz do jogo (onde fica `GTA5_Enhanced.exe`).
2. Compile este projeto (Class Library, .NET Framework 4.8) e copie a `.dll` gerada, junto com `Fleck.dll` e as `NAudio.*.dll`, para a pasta `scripts/` do jogo.
3. Abra o GTA V em Modo História — o mod inicia um servidor WebSocket local em `ws://127.0.0.1:8080` automaticamente.
4. Rode o [bridge Java](#) — ele se conecta nesse WebSocket e passa a mandar as ligações.

---

## 🔌 Protocolo de comunicação

O mod entende uma única mensagem de entrada, vinda do Java:

```
TOCAR_LIGACAO|<nome do chamador>|<texto a ser falado>
```

E manda uma única mensagem de volta, sempre que uma ligação termina (respondida, recusada ou interrompida por erro):

```
LIGACAO_CONCLUIDA
```

É esse sinal de volta que permite ao lado Java saber quando liberar a próxima ligação da fila.

---

## 🛠️ Arquitetura interna

Máquina de estados por trás de cada ligação:

```
Idle → Ringing → Talking → LoadingAnimation → InCall → Ending → Idle
```

| Estado | O que acontece |
|---|---|
| `Ringing` / `Talking` | Toca o toque de chamada, espera o jogador apertar ENTER |
| `LoadingAnimation` | Anexa o celular 3D e espera a animação carregar antes de tocar o áudio |
| `InCall` | Animação travada na orelha, áudio tocando, legendas na tela |
| `Ending` | Pequena folga de tempo antes de liberar os recursos de áudio |

Mensagens recebidas do WebSocket são enfileiradas (`ConcurrentQueue`) e só processadas na thread principal do jogo — nunca direto na thread do Fleck — para evitar corrupção de estado.

---

## ⚠️ Troubleshooting (GTA V Enhanced)

Algumas descobertas específicas dessa versão do jogo, documentadas aqui pra economizar tempo de quem for mexer nisso depois:

- **`GTA.Entity.FromHandle()` e outras APIs gerenciadas do SHVDN podem crashar** (`AccessViolationException` dentro de `NativeMemory.GetEntityAddress`) nessa build do Enhanced. Prefira sempre `Function.Call` com hashes nativos em vez do wrapper gerenciado `GTA.Entity`/`GTA.Prop`.
- **`DELETE_OBJECT` e `DELETE_ENTITY` travam o jogo** ao deletar o prop do celular, mesmo após `DETACH_ENTITY`, mesmo com objeto não-networked. A solução usada aqui: **nunca deletar o objeto** — só desanexar, esconder (`SET_ENTITY_VISIBLE`), tirar colisão e congelar (`FREEZE_ENTITY_POSITION`). O próprio jogo recolhe o objeto sozinho por não ser mission entity.
- Se o jogo **não abrir mais** depois de trocar arquivos do ScriptHookV/SHVDN, geralmente é mistura de versões — apague os arquivos antigos por completo antes de colar os novos.
- Log útil pra debug: `ScriptHookVDotNet.log` na raiz do jogo (exceções .NET capturadas) e o Visualizador de Eventos do Windows → Aplicativo, evento ID 1000/1026 (crashes nativos não capturados pelo log do SHVDN).

---

## 🛣️ Roadmap

- [x] Animação de atender a ligação
- [x] Fim de ligação sem crash
- [x] Sinal de conclusão pro Java (fila de ligações simultâneas)
- [ ] Regras de acesso ao comando (definidas no lado Java)