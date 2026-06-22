using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Ordering.Infrastructure.Migrations;

/// <summary>
/// Adds the optional PayPalOrderId column to the orders table so that
/// PayPal payment captures can reference the originating PayPal order.
/// </summary>
[DbContext(typeof(eShop.Ordering.Infrastructure.OrderingContext))]
[Migration("20260120120000_AddPayPalOrderIdToOrders")]
public partial class AddPayPalOrderIdToOrders : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "PayPalOrderId",
            schema: "ordering",
            table: "orders",
            type: "character varying(64)",
            maxLength: 64,
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "PayPalOrderId",
            schema: "ordering",
            table: "orders");
    }
}


