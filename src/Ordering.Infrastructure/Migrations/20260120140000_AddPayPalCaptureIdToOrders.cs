using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Ordering.Infrastructure.Migrations;

[DbContext(typeof(eShop.Ordering.Infrastructure.OrderingContext))]
[Migration("20260120140000_AddPayPalCaptureIdToOrders")]
public partial class AddPayPalCaptureIdToOrders : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "PayPalCaptureId",
            schema: "ordering",
            table: "orders",
            type: "character varying(64)",
            maxLength: 64,
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "PayPalCaptureId",
            schema: "ordering",
            table: "orders");
    }
}
