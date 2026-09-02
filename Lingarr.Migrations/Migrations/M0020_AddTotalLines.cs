using FluentMigrator;

namespace Lingarr.Migrations.Migrations;

[Migration(20)]
public class M0020_AddTotalLines : Migration
{
    public override void Up()
    {
        if (!Schema.Table("translation_requests").Column("total_lines").Exists())
        {
            Alter.Table("translation_requests")
                .AddColumn("total_lines")
                .AsInt32()
                .NotNullable()
                .WithDefaultValue(0);
        }
    }

    public override void Down()
    {
        Delete.Column("total_lines").FromTable("translation_requests");
    }
}
