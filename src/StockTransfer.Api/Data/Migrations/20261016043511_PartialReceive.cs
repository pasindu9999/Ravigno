using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StockTransfer.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class PartialReceive : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ReceivedQuantity",
                table: "TransferLines",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ReceiveDiscrepancies",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TransferId = table.Column<int>(type: "int", nullable: false),
                    ProductId = table.Column<int>(type: "int", nullable: false),
                    MissingQuantity = table.Column<int>(type: "int", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReceiveDiscrepancies", x => x.Id);
                    table.CheckConstraint("CK_ReceiveDiscrepancies_MissingQuantity_Positive", "[MissingQuantity] > 0");
                    table.ForeignKey(
                        name: "FK_ReceiveDiscrepancies_TransferLines_TransferId_ProductId",
                        columns: x => new { x.TransferId, x.ProductId },
                        principalTable: "TransferLines",
                        principalColumns: new[] { "TransferId", "ProductId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_TransferLines_ReceivedQuantity_Range",
                table: "TransferLines",
                sql: "[ReceivedQuantity] IS NULL OR ([ReceivedQuantity] >= 0 AND [ReceivedQuantity] <= [Quantity])");

            migrationBuilder.CreateIndex(
                name: "IX_ReceiveDiscrepancies_TransferId_ProductId",
                table: "ReceiveDiscrepancies",
                columns: new[] { "TransferId", "ProductId" },
                unique: true);

            // Hand-written. Transfers received before this migration moved their full quantity, so after it
            // NULL only means "not received yet". Status is stored as text (see AppDbContext).
            migrationBuilder.Sql(
                "UPDATE l SET ReceivedQuantity = l.Quantity " +
                "FROM TransferLines l JOIN Transfers t ON t.Id = l.TransferId " +
                "WHERE t.Status = 'Received' AND l.ReceivedQuantity IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReceiveDiscrepancies");

            migrationBuilder.DropCheckConstraint(
                name: "CK_TransferLines_ReceivedQuantity_Range",
                table: "TransferLines");

            migrationBuilder.DropColumn(
                name: "ReceivedQuantity",
                table: "TransferLines");
        }
    }
}
