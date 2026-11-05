# AssetsCraft

App desktop nativo em C# (**Avalonia 11**, .NET 9) para **Windows e Linux** que cria **sprites e sprite sheets
para jogos** conversando com o **Claude Code instalado no seu PC**. Cada **conversa** tem seu workspace com
`tools/sprite.mjs`; o Claude desenha pixel a pixel (ou em SVG), gera a folha e as animações tocam no visualizador
ao lado do chat.

Irmão do VideoCraft: mesma arquitetura (cliente stream-json do Claude Code, conversas, dependências compartilhadas,
limpeza completa, limites de uso), com o pipeline de vídeo/música trocado por um de sprites.

## Como funciona

Não existe SDK oficial do Claude Code para C# (os oficiais, Agent SDK, são TypeScript e Python).
Esses SDKs iniciam o CLI `claude` em modo headless e conversam com ele por JSON em stdin/stdout.
O `ClaudeCodeSession` faz o mesmo em C#:

```
claude -p --input-format stream-json --output-format stream-json --verbose \
       --include-partial-messages --permission-mode acceptEdits --allowedTools ... [--resume <id>]
```

- usa o login/assinatura do seu `claude` (nada de API key no app);
- o processo fica vivo por conversa (contexto mantido); se cair, a próxima mensagem usa `--resume`;
- eventos tipados: texto em streaming, chamadas de ferramenta (Bash, Edit…), resultados, custo/duração;
- "Parar" manda `control_request: interrupt` e mata o processo se ele não responder.

```
src/AssetsCraft.Core/
  Claude/ClaudeCodeSession.cs     cliente do CLI (stream-json)
  Projects/ProjectStore.cs        lista de conversas (projects.json na pasta de dados do app)
  Projects/ProjectCleanup.cs      exclusão completa de uma conversa (pasta + rastros do Claude Code)
  Projects/ConversationFiles.cs   excluir/exportar uma versão de sprite (PNG + JSON + GIFs)
  Claude/UsageMonitor.cs          limites de uso (/usage + rate_limit_event)
  Projects/SharedRuntime.cs       node_modules único compartilhado (link por conversa)
  Sprites/SpriteSheet.cs          lê os metadados da folha (Aseprite JSON: frames + frameTags)
  Sessions/ProjectSession.cs      chat, histórico, dependências, arquivos
  BundledTools.cs                 localiza o FFmpeg embutido e o põe no PATH dos processos filhos
src/AssetsCraft.Desktop/
  Views/SpriteCanvas.cs           visualizador pixel-perfect (vizinho mais próximo, zoom inteiro, xadrez)
  Views/                          janela, chat, ampliação, diálogo de confirmação
template/workspace/               template copiado em cada conversa (CLAUDE.md + tools/sprite.mjs)
scripts/                          fetch-ffmpeg.sh, publish.sh, publish.ps1
```

## Projeto do jogo e tipos de asset

Tudo é feito **para um projeto vinculado** (a pasta do jogo — ex.: o `.csproj` MonoGame; o app detecta MonoGame
pelo `PackageReference` ou por um `.mgcb`). O seletor no topo da barra lateral troca de projeto ou vincula outro
(`<dados do app>/games.json`). Abaixo dele, o menu por tipo de asset:

| Tipo | O que o Claude faz | Destino de "Enviar ao projeto" |
|---|---|---|
| **Sprites** | só a pose **idle**, 1 quadro, sem animação (o `render` recusa mais quadros) | `Content/Sprites/<nome>.png` |
| **Tiles** | tiles estáticos que encaixam, em grade, sem GIF | `Content/Tiles/<nome>.png` |
| **Animação** | parte de um sprite pronto ("Animar este sprite…" ou "Nova animação"): a fonte e o PNG do idle são copiados e o design fica fixo; cada pedido acrescenta uma animação | `Content/Animations/<sprite>.png` |
| SFX, Música, Cutscene | em breve | — |

Sem pasta `Content/`, o destino é a raiz do projeto. "Enviar ao projeto" copia **só o PNG** da versão escolhida
(sobrescreve o anterior, com confirmação) e marca a versão com "no projeto"; o app não edita `Content.mgcb` nem o
código — incorporar o asset fica com quem constrói o jogo. O tipo vai para o Claude nas instruções do sistema e em
`.assetscraft/kind.json`, que o `tools/sprite.mjs` confere.

## Pipeline de sprites (`tools/sprite.mjs`)

O Claude escreve a fonte em `sprites/<nome>.sprite.json` — paleta (própria ou pronta: pico8, sweetie16,
gameboy, endesga32), tamanho do quadro e animações; cada quadro é pixel art em texto (`"....kk.."`), ou
derivado de outro (`base`, `shift`, `flipX`, `patch`, `layers`, `outline`), SVG rasterizado (resvg) ou recorte
de imagem. Depois roda:

```
node tools/sprite.mjs render sprites/heroi.sprite.json
```

que gera uma **nova versão** sem sobrescrever as anteriores:

- `out/heroi-vN.png` — a folha (uma linha por animação; `export.scale`, `padding`, `columns` opcionais);
- `out/heroi-vN.json` — metadados no formato **Aseprite JSON** (`frames` + `meta.frameTags`, com duração de
  cada quadro), aceito por Godot, Phaser, Unity, LÖVE, Defold etc.;
- `out/gif/heroi-vN-<animação>.gif` — um GIF por animação;
- `out/.preview/heroi-vN.png` — folha ampliada com grade, que o Claude **lê para conferir** o desenho antes de responder;
- `out/.sources/heroi-vN.sprite.json` — cópia da fonte daquela versão.

Também há `inspect` (lê um PNG existente como paleta + grade de texto, para editar sprites do usuário),
`preview` (amplia qualquer PNG) e `palettes`. Dependências só em JavaScript puro + resvg com binário pronto:
`pngjs`, `gifenc`, `@resvg/resvg-js`.

## Requisitos

- Claude Code instalado e logado (rode `claude` uma vez para fazer login)
- Node.js 18+
- **Windows:** Git for Windows (requisito do próprio Claude Code)
- Para compilar: .NET 9 SDK. O executável publicado é self-contained (não precisa de .NET).
  Para rodar o build de desenvolvimento só com o runtime .NET 10: `DOTNET_ROLL_FORWARD=Major`.

FFmpeg **não** precisa estar instalado: vai dentro do app (o Claude usa para extrair quadros de vídeos de referência).

## Rodar e publicar

```bash
scripts/fetch-ffmpeg.sh                      # uma vez: baixa o FFmpeg embutido (vendor/ffmpeg)
dotnet run --project src/AssetsCraft.Desktop # desenvolvimento

scripts/publish.sh                           # gera dist/linux-x64 e dist/win-x64
.\scripts\publish.ps1                        # (Windows PowerShell)
```

Distribua a pasta `dist/<rid>` inteira (zip). Executável: `AssetsCraft` (Linux) / `AssetsCraft.exe` (Windows).

## Uso

1. **Nova conversa**: um clique, sem pedir nome. O título é criado sozinho a partir do primeiro pedido
   (na hora, pelo texto; logo depois um título melhor gerado pelo Haiku, sem gravar sessão). Renomear pelo
   menu **⋯** desliga o título automático. Clicar com uma conversa vazia aberta reaproveita essa conversa.
2. **Referências**: no chat, use **+ Anexar referência** ou arraste arquivos para a conversa (imagens, sprites,
   vídeos de movimento, PDFs, textos). Imagens chegam direto ao Claude; vídeos/PDFs são copiados para
   `references/` e o Claude os inspeciona (quadros via ffmpeg, `sprite.mjs inspect`, Read).
   **Assets** (painel direito) são o material que o Claude deve usar de verdade (`assets/`): um sprite para
   animar, uma paleta, um tileset.
3. Peça o sprite no chat (Enter envia, Shift+Enter quebra linha). O resultado aparece em **Sprites**, animando;
   peça ajustes na mesma conversa (cada ajuste vira `-v2`, `-v3`…).
4. **Visualizador**: escolha a animação (chips), pause e avance quadro a quadro (◀ ▶), zoom inteiro
   (ou "Ajustar"), fundo (xadrez = transparente, escuro, claro, grama), velocidade, **Folha inteira** (limites de
   cada quadro, o atual destacado) e **Grade de pixels**. **Ampliar** abre uma janela grande com atalhos
   (Espaço, ← →, ↑ ↓, + −, F, G, B, Esc).
5. **Modelo**: no rodapé da caixa de mensagem escolha Padrão, Fable 5.1, Opus 5.5, Sonnet 5.5 ou Haiku 5.5.
   Vale só para aquela conversa; a troca reinicia o Claude com `--resume`, mantendo o histórico.
6. **Exportar…** copia para uma pasta `<nome>.png` + `<nome>.json` (com `meta.image` apontando para o PNG
   exportado) + `<nome>-<animação>.gif`. No menu **⋯** de cada versão: abrir no visualizador do sistema,
   salvar só o PNG, excluir a versão (PNG, JSON, GIFs, preview e cópia da fonte; a fonte editável continua).

## Dependências compartilhadas

O `node_modules` (pngjs, gifenc, resvg) **vai junto com o app**: o build (`InstallNodeRuntime` no
`AssetsCraft.Desktop.csproj`) roda `npm install` em `obj/node-runtime/<rid>` — só de novo quando o `package.json`
do template muda — e copia para `<app>/runtime`. O binário nativo do resvg é escolhido pelo RID de destino
(`--os/--cpu/--libc`), então `publish.ps1` no Windows também gera o pacote Linux certo. Build e distribuição
precisam de Node/npm na máquina de quem compila; quem usa o app nunca espera instalação.

Cada conversa recebe só um link para esse runtime (symlink no Linux, junction `mklink /J` no Windows — sem
precisar de administrador), então conversa nova fica pronta na hora e conversas antigas são religadas sozinhas
ao abrir. Plano B: se o runtime embutido faltar ou estiver desatualizado, o app instala uma vez em
`<dados do app>/runtime` (hash em `.template-hash`). O Claude é proibido de rodar
`npm install/uninstall/update` e de editar `tools/` (regras em `DisallowedTools` + `CLAUDE.md`), para nenhuma
conversa alterar as dependências ou a ferramenta das outras.

## Dados e exclusão

- O app cria e gerencia as pastas sozinho em `%LOCALAPPDATA%\AssetsCraft` (Windows) /
  `~/.local/share/AssetsCraft` (Linux). O usuário não lida com pastas.
- **Excluir conversa** (menu ⋯ no cabeçalho ou na lista, com confirmação) para o Claude/npm da conversa e
  apaga tudo: pasta interna (fontes, folhas, GIFs, assets, conversa; o link do `node_modules` é desfeito sem tocar
  nas dependências compartilhadas) e os arquivos que o Claude Code guarda por sessão em `~/.claude`
  (`projects/<pasta>/`, `session-env/`, `file-history/`, `todos/`, `debug/`).
- Conversas antigas recebem automaticamente o `CLAUDE.md` e `tools/` atualizados ao abrir.

## Peso de cada conversa

Ao lado do título (e na lista lateral) aparece quanto a conversa ocupa no disco: folhas geradas, GIFs, assets,
referências, histórico e os arquivos que o Claude Code guarda para ela em `~/.claude`. O `node_modules`
compartilhado não entra. O total aparece ao lado de "CONVERSAS".

## Limite de uso do Claude

Rodapé da barra lateral: sessão de 5h, semana (todos os modelos) e semana por modelo, com % e quando reinicia.
Fonte: `claude -p /usage` (não consome uso; roda com `--no-session-persistence` e sem deixar rastro) a cada
10 min e ao fim de cada resposta, mais os `rate_limit_event` que as conversas emitem em tempo real.
Último valor fica em cache para aparecer logo ao abrir o app. Acima de 80% a barra fica vermelha.

## Nenhum processo sobrando

Todo processo filho (Claude Code, ffmpeg/ffprobe, npm, node) é aberto por `ChildProcesses`:
- fechar o app de qualquer jeito (janela, Ctrl+C, SIGTERM/SIGHUP, erro fatal) mata a árvore de cada filho;
- app morto à força: no **Windows** os filhos ficam num *Job Object* `KILL_ON_JOB_CLOSE`; no **Linux** são
  iniciados com `setpriv --pdeathsig KILL` (a partir de uma thread dedicada, para o sinal ficar preso ao app);
- cada filho leva `ASSETSCRAFT_OWNER=<pid>`; ao abrir, o app elimina sobras de execuções anteriores (Linux).

## Configuração (`appsettings.json` → `AssetsCraft`)

Ao lado do executável; sobrescrevível por `settings.json` na pasta de dados do app.

| Chave | Padrão | |
|---|---|---|
| `WorkspacesRoot` | vazio = pasta de dados do app | onde ficam as conversas |
| `Claude:Model` | `null` | `sonnet`, `opus`, `haiku` ou id completo |
| `Claude:PermissionMode` | `acceptEdits` | modo headless não pergunta; o que não está liberado é negado |
| `Claude:SettingSources` | `project,local` | ignora hooks/plugins das configurações globais do usuário (login continua) |
| `Claude:AllowedTools` | Read/Write/Edit, `Bash(node tools/sprite.mjs:*)`, `Bash(ffmpeg:*)`… | regras de permissão do Claude Code |
| `Claude:AppendSystemPrompt` | instruções PT-BR | adicionado ao system prompt |

O comportamento do Claude em cada conversa é guiado pelo `CLAUDE.md` do workspace
(`template/workspace/CLAUDE.md`).

> Segurança: o Claude executa comandos na pasta da conversa. Evite `bypassPermissions` e
> amplie `AllowedTools` só com o necessário.

Interface segue o guia de estilo Linear (Void/Carbon/Obsidian, hairlines, Inter, acid-lime só na ação principal).
