# LucyVNC 2.4.2 handoff

## Objetivo
Corrigir o fluxo do Inno Setup para permitir `0.0.0.0` no modo confirmado, manter padrões LAN/VPN seguros e consistentes, e corrigir a validação de senha sem alegar que VNC clássico usa mais de oito caracteres. Preservar Manager, import/export e separação Server/Manager.

## Estado-base
- Branch: `feature/lucyvnc-2.4-separation`.
- Commit-base confirmado: `6902a2c9affe42482c87627e52dbceb6b7d45ff5` (LucyVNC 2.4.1).
- Worktree inicialmente limpo.
- Causa do bloqueio identificada: `NextButtonClick` valida `ServerConfigurationPage.Values[0]` com `IsAllowedBindAddress` antes de chegar à página de modo; `0.0.0.0` é rejeitado pela função sem permissão explícita. O modo também sobrescrevia o campo, fazendo Back/Next perder o valor de interface específico.
- Limitação de senha identificada: RFB VNC Authentication deriva uma chave DES de oito bytes e ignora bytes de senha após o oitavo. Não existe negociação TLS/VeNCrypt implementada.

## Etapas concluídas
- Investigação do instalador, provisionador, validação do serviço e caminho de autenticação.
- Corrigida a navegação no Setup: endereço de rede efetivo deriva do modo sem sobrescrever o campo, `0.0.0.0` é padrão com modo todas interfaces, checkbox inicia desmarcada, aviso/consentimento precedem a validação. Validação canônica do provisionador é executada antes da gravação protegida após os arquivos terem sido instalados.
- Política Server passa a 6–8 caracteres ASCII imprimíveis; autenticação de baixo nível rejeita >8 sem truncar. O Viewer conserva compatibilidade com peers clássicos aceitando 1–8.
- Atualizados testes de senha, modo de rede e separação; documentação registra que TLS/VeNCrypt e autenticação moderna permanecem pendentes.
- Build Release final passou sem warnings/errors; todos os testes passaram (142/142).
- Os quatro componentes foram publicados self-contained `win-x64` sequencialmente.
- O Inno Setup 6.7.3 compilou `artifacts\LucyVNC-Setup-2.4.2-x64.exe` com os quatro executáveis atualizados nos diretórios de publish.
- SHA-256 do instalador: `69B1501559FC7A7932C0238360506650DB55268FEF1D0854E3618F663CE4E5F2`.
- Validação visual/instalação interativa do assistente não foi executada; o teste de regressão estático do fluxo e a compilação passaram.

## Arquivos modificados
- `installer/LucyVNC.iss`
- `src/Vnc.Protocol/RfbProtocol.cs`
- `src/Vnc.Server/VncServer.cs`
- `src/Vnc.Service/Program.cs`
- `src/Vnc.Service/ServiceConfigurationProvisioner.cs`
- `src/Vnc.Tray/ServerSetupProvisioner.cs`
- `src/Vnc.Tray/LucyVncServerConfigurationForm.cs`
- `src/Vnc.App/CredentialPromptWindow.cs`
- `tests/Vnc.Protocol.Tests/UnitTest1.cs`
- `tests/Vnc.Server.Tests/UnitTest1.cs`
- `tests/Vnc.Service.Tests/ServiceConfigurationProvisionerTests.cs`
- `tests/Vnc.Service.Tests/ServiceSharingControllerTests.cs`
- `tests/Vnc.Service.Tests/ComponentSeparationTests.cs`
- `README.md`
- `docs/PROTOCOL.md`
- `docs/ARCHITECTURE.md`
- `docs/INSTALLATION.md`
- `docs/INTEROPERABILITY.md`
- `docs/CODEX_HANDOFF.md`

## Commits realizados
- Base: `6902a2c9affe42482c87627e52dbceb6b7d45ff5`.
- Implementação: `885b7f4` (`fix: correct setup wildcard flow and VNC password policy`).
- Este handoff registra a validação do commit acima; o ajuste do identificador será commitado separadamente.

## Comandos de build e testes
- `dotnet build LucyVNC.slnx -c Release` — passou (0 warnings, 0 errors).
- `dotnet test LucyVNC.slnx -c Release --no-build` — passou (142/142; Protocol 26, Server 24, Agent 7, Viewer 4, Integration 7, Infrastructure 42, Service 32).
- Restore foi executado pelo build e não encontrou erros.
- `dotnet publish src\Vnc.App\Vnc.App.csproj -c Release -r win-x64 --self-contained true -o artifacts\publish\manager` — passou.
- `dotnet publish src\Vnc.Service\Vnc.Service.csproj -c Release -r win-x64 --self-contained true -o artifacts\publish\server-worker` — passou.
- `dotnet publish src\Vnc.Agent\Vnc.Agent.csproj -c Release -r win-x64 --self-contained true -o artifacts\publish\agent` — passou.
- `dotnet publish src\Vnc.Tray\Vnc.Tray.csproj -c Release -r win-x64 --self-contained true -o artifacts\publish\server-ui` — passou.
- `& 'C:\Users\Zero\AppData\Local\Programs\Inno Setup 6\ISCC.exe' 'installer\LucyVNC.iss'` — compilação bem-sucedida (113.875 s).

## Erros encontrados
- Identificação de causa do bug: validação prematura no evento Next da página de configuração.
- Antes da correção, `VncAuthentication.BuildKey` truncava a entrada; o teste existente esperava que `"password-more"` autenticasse como `"password"`. O comportamento/teste foi corrigido para rejeitar o valor.
- Primeiro build após alterações passou com 1 warning de nullable em `ServerSetupProvisioner`; corrigido com guard explícito. Build final: zero warnings.
- Nenhum teste falhou após correção.
- A primeira compilação Inno do novo fluxo não foi executada até o final antes do ajuste final de versão; a compilação 2.4.2 final foi bem-sucedida.

## Pendências ordenadas
1. Commitar este registro atualizado e confirmar worktree limpo.
2. TLS/VeNCrypt e autenticação moderna sem truncamento estão pendentes; não usar >8 caracteres nem alegar que o tráfego VNC clássico é cifrado.
3. Validação visual/instalação do assistente permanece manual, sem instalar serviço real ou alterar firewall.

## Próxima ação exata
Para o trabalho 2.4.2, o handoff foi commitado localmente em `33a5bc7`; a validação descrita abaixo corresponde à tarefa seguinte 2.4.3.

## LucyVNC 2.4.3 — correção e validação

### Objetivo
Investigar transmissão parcial do framebuffer RFB em 1920×1080 e desconexões associadas à captura, cobrindo o caminho Viewer/Server, limites e serialização, com publicação e Setup 2.4.3. Não alegar interoperabilidade visual TightVNC sem teste real.

### Estado e descobertas
- Branch `feature/lucyvnc-2.4-separation`, HEAD inicial `33a5bc7`; worktree estava limpo.
- Handshake anuncia as dimensões da captura inicial; o Server transmite retângulos Raw recortados às coordenadas/limites da requisição; o Viewer valida bounds e payload ao ler.
- A captura GDI copia linhas com stride e normaliza pixels para BGRA.
- O Agent limita a 1920 de largura e 1920×1080 pixels; a configuração do serviço permite 16 MiB por framebuffer. Um framebuffer 1920×1080×4 ocupa 8.294.400 bytes, portanto os limites examinados não explicam por si só a imagem parcial.
- A captura subsequente ocorre após cada pedido incremental. Falhas da captura atualmente encerram a sessão via tratamento de erro do loop RFB.
- Ainda não foi reproduzido o problema em um TightVNC Viewer real nem identificada uma causa ambiental conclusiva.

### Alterações e validação
- Adicionado teste Viewer↔Server end-to-end que reconstrói e compara byte-a-byte os 2.073.600 pixels de um framebuffer sintético 1920×1080 e confirma explicitamente os quatro cantos.
- O teste completo de integração passou (8/8), incluindo o teste existente de atualização incremental de 64×64.
- A serialização síncrona e assíncrona agora valida todos os retângulos antes de escrever cabeçalho/dados: dimensões/coords RFB, payload Raw por pixel, CopyRect e limite agregado de 64 MiB. Isso evita que payload interno malformado deixe o peer com uma mensagem parcial/desalinhada. Erros reais de escrita continuam propagados.
- Testes direcionados de serialização (2/2) passaram para payload inválido e falha de transmissão simulada. A validação de payload e a reconstrução Full HD são regressões; a causa ambiental do relato original permanece sem reprodução confirmada.
- Atualizado `docs/INTEROPERABILITY.md` separando a evidência sintética Full HD de interoperabilidade visual real; alterado o instalador para 2.4.3.
- Restore: `dotnet restore LucyVNC.slnx` — passou.
- Build: `dotnet build LucyVNC.slnx -c Release --no-restore` — passou, 0 warnings / 0 errors.
- Testes: `dotnet test LucyVNC.slnx -c Release --no-build` — passou, 145/145 (Protocol 28, Server 24, Agent 7, Viewer 4, Integration 8, Infrastructure 42, Service 32).
- Uma primeira tentativa paralela dos testes de Protocol e Integration disputou a escrita de `Vnc.Core.dll` e falhou com CS2012/MSB3026; os testes foram repetidos sequencialmente e passaram. Não foi alterado timeout nem desabilitado teste.
- Publicados self-contained `win-x64`: Manager, Server Worker, Agent e Server UI em `artifacts\publish\{manager,server-worker,agent,server-ui}`.
- Inno Setup 6.7.3 compilou `artifacts\LucyVNC-Setup-2.4.3-x64.exe` com sucesso. Tamanho: 200.942.399 bytes. SHA-256: `135A702E0D83BE0270C89FB615CC1A1F7E7EFFD39C533A5A81636F4162FFEEC8`.
- Nenhum TightVNC Viewer foi usado nesta execução. A matriz mantém a evidência anterior, mas a nova transmissão Full HD foi comprovada apenas por Viewer/Server sintéticos. Captura GDI real, falha real de socket e imagem parcial original não foram reproduzidas.

### Arquivos modificados
- `src/Vnc.Protocol/RfbProtocol.cs`
- `tests/Vnc.Protocol.Tests/UnitTest1.cs`
- `tests/Vnc.Integration.Tests/UnitTest1.cs`
- `installer/LucyVNC.iss`
- `docs/INTEROPERABILITY.md`
- `docs/CODEX_HANDOFF.md`

### Commit
- Implementação validada: `62bcc0b` (`fix: validate RFB framebuffer updates and cover Full HD`).
- Este registro final será commitado separadamente; não fazer push.

### Pendências ordenadas
1. Reproduzir com captura física/GDI em 1920×1080 e correlacionar os logs do Server com uma falha real de transmissão; a causa original relatada não foi identificada conclusivamente.
2. Executar TightVNC Viewer real contra o instalador 2.4.3 e validar imagem/atualizações; mouse/teclado também precisam de comprovação separada.
- Não alterar firewall, instalar/iniciar serviços reais ou fazer push.

### Próxima ação exata
Reproduzir primeiro o problema em uma sessão de teste com TightVNC Viewer e captura GDI real; depois correlacionar os logs do Server. Não repetir restore/build/test/publish, que já passaram para `62bcc0b`, salvo se o código for alterado.

## LucyVNC 2.4.4 — corrigir DPI na captura (em andamento)

### Objetivo
Corrigir virtualização de DPI observada em Windows 11 a 150%, fazendo o Agent anunciar e capturar dimensões físicas sem multiplicadores fixos; preservar coordenadas de entrada e gerar `LucyVNC-Setup-2.4.4-x64.exe`.

### Investigação inicial
- Worktree limpo no commit `ecd662c`; sem manifest de DPI encontrado.
- `GdiScreenCapture` obtém dimensões via `Screen.PrimaryScreen.Bounds`, depois usa `Graphics.CopyFromScreen` e copia linhas via `Bitmap.LockBits`/stride. Ambas as APIs podem refletir coordenadas virtualizadas se o processo não for DPI-aware.
- O processo responsável pela captura é `LucyVNC.Agent`; o Worker pede frames via IPC. A bandeja/Configuration inicializa WinForms e o Worker inicia um host, portanto a awareness deve estar no manifesto e ser confirmada antes da inicialização de cada processo.
- `SendInputController` também usa `Screen.PrimaryScreen.Bounds`; com awareness física, o mapeamento framebuffer→desktop deve continuar proporcional, sem multiplicar coordenadas pelo fator de escala.
- Causa raiz confirmada: não havia DPI awareness declarada nem inicializada nesses processos. Em 150%, Windows virtualizava para coordenadas lógicas (1280×720) a captura de uma tela física 1920×1080.

### Próxima ação exata
Implementar Per-Monitor V2 no Agent/Worker/Server UI, confirmar dimensões e coordenadas físicas, testar o frame Full HD no IPC do Agent, e validar a captura GDI real se o desktop permitir.

### Progresso da implementação
- Configurado `PerMonitorV2` no manifesto do Worker, e via `ApplicationHighDpiMode` nos executáveis WinForms Agent e Server UI; os entry points também chamam `WindowsDpiAwareness.InitializePerMonitorV2()` antes do acesso a captura/WinForms. Falha de inicialização não é ignorada, exceto quando o contexto existente já é PMv2.
- `GdiScreenCapture` verifica a awareness antes de consultar bounds. As dimensões vêm de `Screen.PrimaryScreen.Bounds`, sem fator fixo; `Graphics.CopyFromScreen` e `Bitmap.LockBits` usam as dimensões físicas sob PMv2. O mapeamento de mouse continua proporcional ao retângulo físico informado pelo Windows. A awareness não é imposta no construtor compartilhado de `SendInputController`, para não alterar o contexto de DPI de chamadores WPF; o Agent define PMv2 antes de instanciar captura/input.
- Build Release passou sem warnings/errors após usar `ApplicationHighDpiMode` para evitar o warning WFAC010 de manifests DPI manuais em projetos WinForms.
- Regressões focadas passaram: Agent 8/8 (inclui Full HD no IPC), mapeamento de coordenadas 4/4 e configuração dos executáveis 1/1; integração passou 9/9, incluindo inicialização PMv2 em processo isolado.
- Captura real validada no desktop disponível: `GetDpiForSystem=144` (150%), GDI retornou 1920×1080 e uma sessão RFB Viewer/Server local recebeu um Raw completo 1920×1080 com 8.294.400 bytes. Nenhum pixel foi salvo nem exibido; não foi testado com TightVNC nesta etapa.
- Na primeira execução do probe, a senha de teste excedia 8 caracteres; corrigida para uma senha válida de teste e o probe passou. Duas instâncias Agent/Manager, Server UI e Worker observadas no desktop antes/depois eram preexistentes; não foram iniciadas, paradas nem alteradas por esta tarefa.
- A primeira suíte completa revelou que a inicialização PMv2 dentro do construtor compartilhado de `SendInputController` falha no processo de testes WPF já inicializado com outro contexto. Removida a mudança global do construtor: PMv2 continua aplicado antes de qualquer UI/captura nos entry points do Agent/Worker/Tray, e o construtor de captura mantém verificação. Uma segunda suíte teve falha transitória de EOF em um teste de named pipe do Agent; o projeto do Agent passou isoladamente e a suíte completa seguinte passou sem alterações de timeout.

### Próxima ação exata
Restore: `dotnet restore LucyVNC.slnx` passou. Build: `dotnet build LucyVNC.slnx -c Release --no-restore` passou sem warnings/errors. A suíte final passou 152/152 (Protocol 28, Server 28, Agent 8, Viewer 4, Integration 9, Infrastructure 42, Service 33).

Publicados Manager, Worker, Agent e Server UI self-contained `win-x64`. Inno Setup 6.7.3 compilou `artifacts\LucyVNC-Setup-2.4.4-x64.exe` (200.943.542 bytes; SHA-256 `BB68B243717AA8968191B09F5F72FE5FF6DD2E49D45F5D16E5554F5E18D2D23A`).

Captura real/GDI e RFB foram testados no monitor atual a 144 DPI (150%): dimensões 1920×1080 e um Raw completo de 8.294.400 bytes chegou ao Viewer local. O frame não foi salvo nem visualizado. As escalas 100%, 125%, 175% e 200% não foram testadas individualmente. TightVNC Viewer real, SendInput físico e o Worker/Agent via serviço SCM não foram testados nesta alteração.

Commit local de implementação: `a6317a2` (`fix: enable per-monitor DPI capture for LucyVNC Server`). Este ajuste final do handoff será commitado separadamente; sem push e sem operações sobre os serviços/processos existentes.

Arquivos alterados: `src/Vnc.Platform.Windows/LucyVNC.Dpi.manifest`, `WindowsDpiAwareness.cs`, `ScreenCapture.cs`, `InputInjection.cs`; `src/Vnc.Agent/Program.cs`, `Vnc.Agent.csproj`; `src/Vnc.Service/Program.cs`, `Vnc.Service.csproj`; `src/Vnc.Tray/Program.cs`, `Vnc.Tray.csproj`; `tests/Vnc.Agent.Tests/LucySessionAgentTests.cs`, `tests/Vnc.Server.Tests/UnitTest1.cs`, `tests/Vnc.Service.Tests/ComponentSeparationTests.cs`, `tests/Vnc.ProcessHost/Program.cs`, `Vnc.ProcessHost.csproj`, `tests/Vnc.Integration.Tests/UnitTest1.cs`; `installer/LucyVNC.iss`; `docs/CODEX_HANDOFF.md`.

## LucyVNC 2.4.5 — falha de inicialização do Worker (em andamento)

### Objetivo
Remover do Windows Service qualquer configuração gráfica DPI que cause falha no startup; preservar PMv2 no Agent, captura em 150%, configuração 0.0.0.0:5900 e produzir Setup 2.4.5. Avaliar transferência do Viewer sem implementar transporte inseguro.

### Causa confirmada
- O Worker recebeu o manifesto do Agent (PMv2) e chamava `SetProcessDpiAwarenessContext` antes de iniciar o Host. O serviço não usa APIs gráficas; em contexto de serviço a chamada não é necessária e pode retornar acesso negado, abortando `Main`.
- Corrigir removendo chamada DPI e manifesto apenas do Worker. O Agent continua sendo o dono da captura interativa e mantém PMv2.

### Transferência Viewer
- Inspeção inicial: Viewer usa conexão TCP/RFB convencional; autenticação VNC clássica não cifra o fluxo. Não existe `SslStream`, negociação TLS ou canal seguro de transferência no caminho Viewer/Server. Não enviar comandos de arquivo no fluxo RFB sem negociação e proteção autenticada, nem desenhar botão que aparente funcionalidade inexistente.
- Transferência permanecerá pendente/desabilitada nesta entrega; não foi iniciada implementação UI insegura. Próxima etapa precisará de negociação LucyVNC explícita e canal criptografado autenticado, ou uma arquitetura segura aprovada.

### Marco A concluído
- Removido do Worker `WindowsDpiAwareness.InitializePerMonitorV2()` e removido do `Vnc.Service.csproj` o manifesto DPI. O entry point segue direto para configuração/host e não inicializa User32 DPI.
- O manifesto PMv2 permanece aplicado ao Agent; `GdiScreenCapture` continua a exigir DPI awareness e obter bounds físicos, sem multiplicadores.
- Testes passaram: regressão de configuração Worker/Agent 1 teste; processo Worker+Agent+Viewer loopback 1 teste; captura Full HD IPC Agent 1 teste.
- Primeira tentativa paralela de testes disputou arquivo intermediário `Vnc.Core.dll` com Microsoft Defender (CS2012); repetição sequencial passou (2/2). Sem alteração de timeout.

### Próxima ação exata
Marco A e pacote 2.4.5 concluídos:
- Restore `dotnet restore LucyVNC.slnx` passou.
- Build `dotnet build LucyVNC.slnx -c Release --no-restore` passou sem warnings/errors.
- Suíte `dotnet test LucyVNC.slnx -c Release --no-build` passou 152/152 (Protocol 28, Server 28, Agent 8, Viewer 4, Integration 9, Infrastructure 42, Service 33). Uma falha inicial foi uma asserção de versão ainda referindo 2.4.4 após atualizar o instalador; asserção atualizada para 2.4.5, teste focado e suíte completa passaram. Não se enfraqueceu teste.
- Publicados Manager, Worker, Agent e Server UI self-contained `win-x64`.
- Inno Setup 6.7.3 compilou `artifacts\LucyVNC-Setup-2.4.5-x64.exe` (200.937.335 bytes; SHA-256 `5C734E4B107B2A86E230324E6C83B27EC586A28BEA9FF3401A0710F5FDD47F72`).
- O teste existente Worker+Agent+Viewer loopback passou; prova inicial funcional do worker e conexão RFB em modo console/teste, não inicialização SCM real ou serviço instalado. Nenhum serviço foi instalado/iniciado nesta tarefa.
- A captura física a 150% (144 DPI, 1920×1080 e Raw completo) continua comprovada pelo probe real da 2.4.4; o código do Agent/captura não foi alterado nesta etapa. Nenhum teste a outras escalas.
- Commit de código do Marco A: `4d52a55` (`fix: remove DPI initialization from Windows Worker`). O ajuste de asserção de versão e este relatório final serão commitados juntos.
- Transferência de arquivos não implementada nem exibida na UI: RFB atual é plaintext após autenticação DES, sem TLS nem negociação de capacidade/canal proprietário. Implementar transferência neste transporte violaria o requisito de criptografia/autorização. Próxima ação de transferência: projetar e negociar canal criptográfico autenticado com escopo de sessão antes de qualquer UI/operação de arquivos.
- Worktree será conferido limpo após commit final. Não fazer push/merge nem operações no firewall/serviço real.

### Próxima ação exata
Commitar `tests/Vnc.Service.Tests/ComponentSeparationTests.cs` e este handoff, verificar `git status --short --branch`; em sessão futura, iniciar transferência apenas após definir canal seguro negociado, ou validar Setup 2.4.5/SCM em máquina de teste autorizada.
