using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Ordering.Infrastructure.Migrations;

[DbContext(typeof(eShop.Ordering.Infrastructure.OrderingContext))]
[Migration("20260120130000_AddPayPalAuthorizationIdToOrders")]
public partial class AddPayPalAuthorizationIdToOrders : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "PayPalAuthorizationId",
            schema: "ordering",
            table: "orders",
            type: "character varying(256)",
            maxLength: 256,
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "PayPalAuthorizationId",
            schema: "ordering",
            table: "orders");
    }
}
