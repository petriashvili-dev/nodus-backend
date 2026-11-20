using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nodus.Api.Infrastructure.Persistence.Migrations;

public partial class SeriesOccurrenceReties : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.AddColumn<string>(name: "replaces_occurrence_key", table: "node_revisions",
            type: "character varying(32)", maxLength: 32, nullable: true);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropColumn(name: "replaces_occurrence_key", table: "node_revisions");
}
