using FluentMigrator;

namespace Lingarr.Migrations.Migrations;

[Migration(21)]
public class M0021_SeedCaptionSatisfiesTarget : Migration
{
    public override void Up()
    {
        Insert.IntoTable("settings").Row(new { key = "caption_satisfies_target", value = "true" });
    }

    public override void Down()
    {
        Delete.FromTable("settings").Row(new { key = "caption_satisfies_target" });
    }
}
