<%@ Page Title="Orders" Language="vb" AutoEventWireup="false" MasterPageFile="~/Site.Master" CodeBehind="Orders.aspx.vb" Inherits="SampleApp.Orders" %>
<%@ Register Src="~/Controls/Widget.ascx" TagPrefix="uc" TagName="Widget" %>

<asp:Content ID="Content1" ContentPlaceHolderID="Main" runat="server">
    <asp:Label ID="lblCustomer" runat="server" AssociatedControlID="txtCustomer" Text="Customer" />
    <asp:TextBox ID="txtCustomer" runat="server" TextMode="SingleLine" AutoPostBack="true" OnTextChanged="txtCustomer_TextChanged" />
    <asp:RequiredFieldValidator ID="rfvCustomer" runat="server" ControlToValidate="txtCustomer" Display="Dynamic" ErrorMessage="Required" />

    <asp:DropDownList ID="ddlStatus" runat="server">
        <asp:ListItem Value="open" Text="Open" />
        <asp:ListItem Value="closed" Text="Closed" />
    </asp:DropDownList>

    <asp:GridView ID="gvOrders" runat="server" AutoGenerateColumns="false" OnRowCommand="gvOrders_RowCommand">
        <HeaderStyle CssClass="head" />
        <Columns>
            <asp:BoundField DataField="Id" HeaderText="Order" />
            <asp:TemplateField HeaderText="Actions">
                <ItemTemplate>
                    <asp:LinkButton ID="lbOpen" runat="server" CommandName="Open" Text="Open" />
                </ItemTemplate>
            </asp:TemplateField>
        </Columns>
    </asp:GridView>

    <asp:Panel ID="pnlActions" runat="server">
        <asp:Button ID="btnSearch" runat="server" Text="Search" OnClick="btnSearch_Click" />
        <asp:Button ID="btnReset" runat="server" Text="Reset" />
    </asp:Panel>

    <uc:Widget ID="widgetSummary" runat="server" />
</asp:Content>
