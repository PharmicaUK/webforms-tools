Public Class Orders
    Inherits System.Web.UI.Page

    Protected Sub btnSearch_Click(sender As Object, e As EventArgs)
        gvOrders.DataBind()
        lblCustomer.Text = txtCustomer.Text
    End Sub

    Private Sub btnReset_Click(sender As Object, e As EventArgs) Handles btnReset.Click
        txtCustomer.Text = ""
    End Sub

    Protected Sub txtCustomer_TextChanged(sender As Object, e As EventArgs)
    End Sub

    Protected Sub gvOrders_RowCommand(sender As Object, e As GridViewCommandEventArgs)
    End Sub

    Protected Sub NoArguments()
    End Sub

    Protected Sub WrongArguments(sender As Object, e As GridViewCommandEventArgs)
    End Sub

    Protected Sub Validate(source As Object, args As ServerValidateEventArgs)
    End Sub

    Protected Sub Page_Load(sender As Object, e As EventArgs) Handles Me.Load
    End Sub

    Friend Sub FriendSub(sender As Object, e As EventArgs)
    End Sub

    Protected Friend Sub ProtectedFriendSub(sender As Object, e As EventArgs)
    End Sub

    Sub NoModifierSub(sender As Object, e As EventArgs)
    End Sub

    Protected Function NotASub(sender As Object, e As EventArgs) As Boolean
        Return True
    End Function
End Class
