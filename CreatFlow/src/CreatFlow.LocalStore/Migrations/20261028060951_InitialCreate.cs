using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CreatFlow.LocalStore.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "x_async_task_item",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    task_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    target_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    target_kind = table.Column<int>(type: "INTEGER", nullable: false),
                    group_key = table.Column<Guid>(type: "TEXT", nullable: false),
                    platform_key = table.Column<string>(type: "TEXT", nullable: false),
                    platform_session_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    session_label = table.Column<string>(type: "TEXT", nullable: true),
                    platform_object_id = table.Column<string>(type: "TEXT", nullable: true),
                    status = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    error_msg = table.Column<string>(type: "TEXT", nullable: true),
                    retry_count = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    finished_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_x_async_task_item", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "x_async_tasks",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    member_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    kind = table.Column<int>(type: "INTEGER", nullable: false),
                    title = table.Column<string>(type: "TEXT", nullable: false),
                    status = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    total_count = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    done_count = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    failed_count = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_x_async_tasks", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "x_collection_platforms",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    collection_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    platform_key = table.Column<string>(type: "TEXT", nullable: false),
                    platform_session_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    platform_collection_id = table.Column<string>(type: "TEXT", nullable: true),
                    title = table.Column<string>(type: "TEXT", nullable: true),
                    description = table.Column<string>(type: "TEXT", nullable: true),
                    cover_image = table.Column<string>(type: "TEXT", nullable: true),
                    status = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    error_msg = table.Column<string>(type: "TEXT", nullable: true),
                    collection_type = table.Column<string>(type: "TEXT", nullable: true),
                    extra_data = table.Column<string>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_x_collection_platforms", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "x_collections",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    member_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    title = table.Column<string>(type: "TEXT", nullable: false),
                    description = table.Column<string>(type: "TEXT", nullable: true),
                    cover_image_path = table.Column<string>(type: "TEXT", nullable: true),
                    cover_image_url = table.Column<string>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_x_collections", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "x_member",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    username = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    password_hash = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    phone = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    email = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    expire_date = table.Column<DateTime>(type: "TEXT", nullable: false),
                    is_active = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    last_login_ip = table.Column<string>(type: "TEXT", maxLength: 45, nullable: true),
                    last_login_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_x_member", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "x_platform_sessions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    member_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    platform_key = table.Column<string>(type: "TEXT", nullable: false),
                    account_label = table.Column<string>(type: "TEXT", nullable: true),
                    is_primary = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    platform_uid = table.Column<string>(type: "TEXT", nullable: true),
                    platform_name = table.Column<string>(type: "TEXT", nullable: true),
                    platform_avatar = table.Column<string>(type: "TEXT", nullable: true),
                    cookie = table.Column<string>(type: "TEXT", nullable: true),
                    local_storage = table.Column<string>(type: "TEXT", nullable: true),
                    extra_data = table.Column<string>(type: "TEXT", nullable: true),
                    status = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    logged_in_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    probed_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    probed_result = table.Column<string>(type: "TEXT", nullable: true),
                    expired_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_x_platform_sessions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "x_platforms",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    platform_key = table.Column<string>(type: "TEXT", nullable: false),
                    display_name = table.Column<string>(type: "TEXT", nullable: false),
                    brand_color_hex = table.Column<string>(type: "TEXT", nullable: false),
                    login_url = table.Column<string>(type: "TEXT", nullable: false),
                    estimated_valid_days = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 3),
                    is_enabled = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    sort_order = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_x_platforms", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "x_publish_platforms",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    publish_task_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    platform_key = table.Column<string>(type: "TEXT", nullable: false),
                    platform_session_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    title = table.Column<string>(type: "TEXT", nullable: true),
                    description = table.Column<string>(type: "TEXT", nullable: true),
                    cover_file_path = table.Column<string>(type: "TEXT", nullable: true),
                    cover_url = table.Column<string>(type: "TEXT", nullable: true),
                    collection_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    extra_data_json = table.Column<string>(type: "TEXT", nullable: true),
                    status = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    progress_pct = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    error_msg = table.Column<string>(type: "TEXT", nullable: true),
                    retry_count = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    hidden_in_queue = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    platform_video_id = table.Column<string>(type: "TEXT", nullable: true),
                    platform_video_url = table.Column<string>(type: "TEXT", nullable: true),
                    published_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_x_publish_platforms", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "x_publish_tasks",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    member_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    video_file_path = table.Column<string>(type: "TEXT", nullable: false),
                    video_file_name = table.Column<string>(type: "TEXT", nullable: false),
                    video_file_size = table.Column<long>(type: "INTEGER", nullable: false, defaultValue: 0L),
                    video_hash = table.Column<string>(type: "TEXT", nullable: false),
                    video_duration_s = table.Column<int>(type: "INTEGER", nullable: true),
                    video_width = table.Column<int>(type: "INTEGER", nullable: true),
                    video_height = table.Column<int>(type: "INTEGER", nullable: true),
                    cover_file_path = table.Column<string>(type: "TEXT", nullable: true),
                    common_title = table.Column<string>(type: "TEXT", nullable: true),
                    common_desc = table.Column<string>(type: "TEXT", nullable: true),
                    scheduled_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    status = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_x_publish_tasks", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "idx_async_task_item_group_key",
                table: "x_async_task_item",
                column: "group_key");

            migrationBuilder.CreateIndex(
                name: "idx_async_task_item_session_id",
                table: "x_async_task_item",
                column: "platform_session_id");

            migrationBuilder.CreateIndex(
                name: "idx_async_task_item_status",
                table: "x_async_task_item",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "idx_async_task_item_target_id",
                table: "x_async_task_item",
                column: "target_id");

            migrationBuilder.CreateIndex(
                name: "idx_async_task_item_task_id",
                table: "x_async_task_item",
                column: "task_id");

            migrationBuilder.CreateIndex(
                name: "idx_async_tasks_created_at",
                table: "x_async_tasks",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "idx_async_tasks_member_id",
                table: "x_async_tasks",
                column: "member_id");

            migrationBuilder.CreateIndex(
                name: "idx_async_tasks_status",
                table: "x_async_tasks",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "idx_collection_platforms_collection_id",
                table: "x_collection_platforms",
                column: "collection_id");

            migrationBuilder.CreateIndex(
                name: "idx_collection_platforms_platform_key",
                table: "x_collection_platforms",
                column: "platform_key");

            migrationBuilder.CreateIndex(
                name: "idx_collection_platforms_session_id",
                table: "x_collection_platforms",
                column: "platform_session_id");

            migrationBuilder.CreateIndex(
                name: "idx_collections_member_id",
                table: "x_collections",
                column: "member_id");

            migrationBuilder.CreateIndex(
                name: "IX_x_member_phone",
                table: "x_member",
                column: "phone",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_x_member_username",
                table: "x_member",
                column: "username",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_platform_sessions_member_platform",
                table: "x_platform_sessions",
                columns: new[] { "member_id", "platform_key" });

            migrationBuilder.CreateIndex(
                name: "idx_platform_sessions_platform_status",
                table: "x_platform_sessions",
                columns: new[] { "platform_key", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_x_platforms_platform_key",
                table: "x_platforms",
                column: "platform_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_publish_platforms_platform_key",
                table: "x_publish_platforms",
                column: "platform_key");

            migrationBuilder.CreateIndex(
                name: "idx_publish_platforms_session_id",
                table: "x_publish_platforms",
                column: "platform_session_id");

            migrationBuilder.CreateIndex(
                name: "idx_publish_platforms_status",
                table: "x_publish_platforms",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "idx_publish_platforms_task_id",
                table: "x_publish_platforms",
                column: "publish_task_id");

            migrationBuilder.CreateIndex(
                name: "idx_publish_tasks_created_at",
                table: "x_publish_tasks",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "idx_publish_tasks_member_id",
                table: "x_publish_tasks",
                column: "member_id");

            migrationBuilder.CreateIndex(
                name: "idx_publish_tasks_status",
                table: "x_publish_tasks",
                column: "status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "x_async_task_item");

            migrationBuilder.DropTable(
                name: "x_async_tasks");

            migrationBuilder.DropTable(
                name: "x_collection_platforms");

            migrationBuilder.DropTable(
                name: "x_collections");

            migrationBuilder.DropTable(
                name: "x_member");

            migrationBuilder.DropTable(
                name: "x_platform_sessions");

            migrationBuilder.DropTable(
                name: "x_platforms");

            migrationBuilder.DropTable(
                name: "x_publish_platforms");

            migrationBuilder.DropTable(
                name: "x_publish_tasks");
        }
    }
}
