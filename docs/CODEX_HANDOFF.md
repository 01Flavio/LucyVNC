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
Executar `git add docs/CODEX_HANDOFF.md` e criar commit local de documentação; depois verificar `git status --short --branch`.

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
- Pendente; criar commit local após verificação final do diff. Não fazer push.

### Pendências ordenadas
1. Reproduzir com captura física/GDI em 1920×1080 e correlacionar os logs do Server com uma falha real de transmissão; a causa original relatada não foi identificada conclusivamente.
2. Executar TightVNC Viewer real contra o instalador 2.4.3 e validar imagem/atualizações; mouse/teclado também precisam de comprovação separada.
3. Atualizar o hash/commit deste handoff após o commit local.
- Não alterar firewall, instalar/iniciar serviços reais ou fazer push.
