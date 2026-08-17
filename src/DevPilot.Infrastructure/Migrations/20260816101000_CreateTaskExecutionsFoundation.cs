using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DevPilot.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class CreateTaskExecutionsFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'TaskExecutions') THEN
        CREATE TABLE ""TaskExecutions"" (
            ""Id"" uuid NOT NULL,
            ""DevelopmentTaskId"" uuid NOT NULL,
            ""Status"" character varying(50) NOT NULL,
            ""CreatedAt"" timestamp with time zone NOT NULL,
            ""StartedAt"" timestamp with time zone NULL,
            ""CompletedAt"" timestamp with time zone NULL,
            ""ErrorMessage"" character varying(4000) NULL,
            CONSTRAINT ""PK_TaskExecutions"" PRIMARY KEY (""Id""),
            CONSTRAINT ""FK_TaskExecutions_DevelopmentTasks_DevelopmentTaskId"" FOREIGN KEY (""DevelopmentTaskId"") REFERENCES ""DevelopmentTasks"" (""Id"") ON DELETE CASCADE
        );

        CREATE INDEX ""IX_TaskExecutions_DevelopmentTaskId_Status"" ON ""TaskExecutions"" (""DevelopmentTaskId"", ""Status"");

        CREATE UNIQUE INDEX ""IX_TaskExecutions_ActivePerTask"" ON ""TaskExecutions"" (""DevelopmentTaskId"") WHERE ""Status"" IN ('Pending', 'Running');
    END IF;
END $$;
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'TaskExecutions') THEN
        DROP TABLE ""TaskExecutions"" CASCADE;
    END IF;
END $$;
");
        }
    }
}
