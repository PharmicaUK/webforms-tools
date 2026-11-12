<%@ Page Title="Home" Language="vb" AutoEventWireup="false" MasterPageFile="~/Site.Master" CodeBehind="Default.aspx.vb" Inherits="SampleApp._Default" %>
<%@ MasterType VirtualPath="~/Site.Master" %>
<%@ Register Src="~/Controls/Widget.ascx" TagPrefix="uc" TagName="Widget" %>
<%@ Register Src="Controls/NoCode.ascx" TagPrefix="uc" TagName="NoCode" %>

<script runat="server">
    ' <asp:Label ID="lblInScript" runat="server" /> is code, not markup
</script>

<asp:Content ID="Content1" ContentPlaceHolderID="Main" runat="server">
    <div runat="server" id="divOuter">
        <div class="literal">
            <div>nested literal divs must not close divOuter early</div>
        </div>
        <asp:Literal ID="litInsideOuter" runat="server" />
    </div>

    <asp:TextBox ID="txtName" runat="server" Text='<%# Eval("Name") %>' />
    <asp:Button ID="btnSave" runat="server" Text="Save" OnClick="btnSave_Click" />
    <asp:DropDownList ID="ddlChoice" runat="server">
        <asp:ListItem Value="1" Text="One" />
    </asp:DropDownList>

    <input type="text" id="inpText" runat="server" />
    <input type="checkbox" id="inpCheck" runat="server">
    <input type="email" id="inpEmail" runat="server" />
    <input id="inpNoType" runat="server" />
    <a id="lnkHome" runat="server" href="~/">Home</a>
    <span id="spanGeneric" runat="server"></span>
    <iframe id="frame1" runat="server"></iframe>

    <table id="tblData" runat="server">
        <tr id="rowFirst">
            <td id="cellFirst" runat="server"><asp:Label ID="lblInCell" runat="server" /></td>
        </tr>
        <%-- HtmlTable.Rows is [IgnoreUnknownContent]: colgroup/tfoot tags are dropped, their rows are kept --%>
        <colgroup><col width="50%" /></colgroup>
        <tfoot>
            <tr id="rowInFoot">
                <td><asp:Label ID="lblInFoot" runat="server" /></td>
            </tr>
        </tfoot>
    </table>

    <asp:Repeater ID="rptItems" runat="server">
        <ItemTemplate>
            <asp:Label ID="lblInTemplate" runat="server" />
        </ItemTemplate>
    </asp:Repeater>

    <asp:UpdatePanel ID="upMain" runat="server">
        <ContentTemplate>
            <asp:Label ID="lblInUpdatePanel" runat="server" />
        </ContentTemplate>
    </asp:UpdatePanel>

    <asp:GridView ID="gvRows" runat="server">
        <Columns>
            <asp:BoundField DataField="Id" />
            <asp:TemplateField>
                <ItemTemplate><asp:HyperLink ID="hlInGrid" runat="server" /></ItemTemplate>
            </asp:TemplateField>
        </Columns>
    </asp:GridView>

    <asp:MultiView ID="mvSteps" runat="server">
        <asp:View ID="vwOne" runat="server"><asp:Panel ID="pnlInView" runat="server" /></asp:View>
    </asp:MultiView>

    <uc:Widget ID="widget1" runat="server" />
    <uc:NoCode ID="noCode1" runat="server" />
    <site:Footer ID="footer1" runat="server" />

    <!-- <asp:Label ID="lblInHtmlComment" runat="server" /> -->
    <%-- <asp:Label ID="lblInServerComment" runat="server" /> --%>

    <asp:Label ID="lblDeclaredInCodeBehind" runat="server" />
    <asp:Label ID="lblDynamic<%= 1 %>" runat="server" />
</asp:Content>
