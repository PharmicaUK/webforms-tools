// Integration test: downloads VS Code (cached in .vscode-test/), opens a temp copy of the SampleApp fixture with this
// extension in development mode, and runs suite.ts inside it against the `webforms` on PATH (or WEBFORMS_SERVER).
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import { runTests } from '@vscode/test-electron';

const extensionRoot = path.resolve(__dirname, '../..');
const repoRoot = path.resolve(extensionRoot, '../..');

const page = `<%@ Page Language="vb" AutoEventWireup="false" CodeBehind="Probe.aspx.vb" Inherits="SampleApp.Probe" %>
<html><body>
<form id="form1" runat="server">
<%-- a server comment --%>
<p><%= Greeting.ToUpper() %></p>
<asp:Button ID="btnGo" runat="server" Text="Go" OnClick="btnGo_Click" />
<asp:Label ID="lblTip" runat="server" Text="<%$ AppSettings:Tip %>" />
<asp:Label ID="lblBound" runat="server" Text='<%# Greeting %>' />
<asp:Repeater ID="rptLines" runat="server"><ItemTemplate><%# Container.ItemIndex %></ItemTemplate></asp:Repeater>
</form>
<script runat="server">
    Protected Function Shout(s As String) As String
        Return s.ToUpper()
    End Function
</script>
<script>var clientSide = 1;</script>
</body></html>
`;

const codeBehind = `Partial Public Class Probe
    Inherits System.Web.UI.Page

    Protected Greeting As String = "hi"

    Protected Sub btnGo_Click(sender As Object, e As EventArgs)
    End Sub
End Class
`;

async function main(): Promise<void> {
  const workspace = fs.mkdtempSync(path.join(os.tmpdir(), 'webforms-vscode-'));
  fs.cpSync(path.join(repoRoot, 'tests/WebForms.Tests/Fixtures/SampleApp'), workspace, { recursive: true });
  fs.writeFileSync(path.join(workspace, 'Probe.aspx'), page);
  fs.writeFileSync(path.join(workspace, 'Probe.aspx.vb'), codeBehind);
  fs.writeFileSync(path.join(workspace, 'Unformatted.vb'), 'public class Unformatted\nsub M()\ndim x as integer = 1\nend sub\nend class\n');
  const vbproj = path.join(workspace, 'SampleApp.vbproj');
  fs.writeFileSync(vbproj, fs.readFileSync(vbproj, 'utf8').replace('  </ItemGroup>', '    <Compile Include="Probe.aspx.vb" />\r\n  </ItemGroup>'));

  const userSettings = path.join(workspace, '.vscode');
  fs.mkdirSync(userSettings, { recursive: true });
  const server = process.env.WEBFORMS_SERVER;
  const log = process.env.WEBFORMS_TEST_SERVER_LOG; // the server's request log (CI uploads it on failure)
  fs.writeFileSync(path.join(userSettings, 'settings.json'), JSON.stringify({
    ...(server ? { 'webforms.server.path': server } : {}),
    ...(log ? { 'webforms.server.log': path.resolve(log) } : {}),
  }, null, 2));

  try {
    await runTests({
      version: process.env.VSCODE_TEST_VERSION ?? 'stable',
      extensionDevelopmentPath: extensionRoot,
      extensionTestsPath: path.join(__dirname, 'suite.js'),
      launchArgs: [workspace, '--disable-workspace-trust', '--skip-welcome', '--skip-release-notes'],
      extensionTestsEnv: { WEBFORMS_TEST_WORKSPACE: workspace },
    });
  } finally {
    fs.rmSync(workspace, { recursive: true, force: true });
  }
}

main().catch(err => {
  console.error(err);
  process.exit(1);
});
