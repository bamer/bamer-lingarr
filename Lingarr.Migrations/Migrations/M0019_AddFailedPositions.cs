using FluentMigrator;

namespace Lingarr.Migrations.Migrations;

[Migration(19)]
public class M0019_AddFailedPositions : Migration
{
    public override void Up()
    {
        if (!Schema.Table("translation_requests").Column("failed_positions").Exists())
        {
            Alter.Table("translation_requests")
                .AddColumn("failed_positions")
                .AsString()
                .Nullable();
        }
    }

    public override void Down()
    {
        Delete.Column("failed_positions").FromTable("translation_requests");
    }
}
