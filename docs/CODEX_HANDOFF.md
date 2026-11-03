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
