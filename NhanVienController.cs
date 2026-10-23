using System.Security.Claims;
using CinemaBookingCS.Data;
using CinemaBookingCS.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CinemaBookingCS.Controllers;

// QD21/QD22: chỉ Admin hệ thống được quản lý tài khoản nhân viên
[Authorize(Roles = "Admin")]
public class NhanVienController : Controller
{
    private readonly ApplicationDbContext _db;
    public NhanVienController(ApplicationDbContext db) => _db = db;
    private string MaNguoiDungHienTai => User.FindFirst(ClaimTypes.NameIdentifier)!.Value;

    public async Task<IActionResult> DanhSach()
    {
        var ds = await _db.NhanViens.OrderBy(n => n.MaNv).ToListAsync();
        return View(ds);
    }

    // ================= BM18 - Phiếu quản lý tài khoản nhân viên =================
    [HttpGet]
    public IActionResult TaoMoi() => View();

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> TaoMoi(string hoTen, string email, string sdt,
        PhongBan phongBan, VaiTro vaiTro, TrangThaiNhanVien trangThai)
    {
        email = (email ?? "").Trim().ToLower();

        if (await _db.NhanViens.AnyAsync(x => x.Email == email))
        {
            ViewBag.Loi = "Email này đã được sử dụng cho tài khoản nhân viên khác.";
            return View();
        }

        // Mật khẩu mặc định khi Admin tạo mới - NV nên đổi ở lần đăng nhập đầu (TODO: thêm luồng đổi MK)
        const string matKhauMacDinh = "NhanVien@123";

        var nv = new NhanVien
        {
            MaNv = "NV" + Guid.NewGuid().ToString("N")[..6].ToUpper(),
            HoTen = hoTen,
            Email = email,
            MatKhau = BCrypt.Net.BCrypt.HashPassword(matKhauMacDinh),
            Sdt = sdt,
            PhongBan = phongBan,
            VaiTro = vaiTro,
            TrangThai = trangThai,
            MaNvTao = MaNguoiDungHienTai,
            NgayTao = DateTime.Now
        };
        _db.NhanViens.Add(nv);
        await _db.SaveChangesAsync();

        TempData["ThongBao"] = $"Đã tạo tài khoản nhân viên \"{hoTen}\". Mật khẩu mặc định: {matKhauMacDinh}";
        return RedirectToAction(nameof(DanhSach));
    }

    // Cập nhật trạng thái/vai trò nhân viên
    [HttpGet]
    public async Task<IActionResult> ChinhSua(string id)
    {
        var nv = await _db.NhanViens.FindAsync(id);
        if (nv == null) return NotFound();
        return View(nv);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ChinhSua(string maNv, string hoTen, string sdt,
        PhongBan phongBan, VaiTro vaiTro, TrangThaiNhanVien trangThai)
    {
        var nv = await _db.NhanViens.FindAsync(maNv);
        if (nv == null) return NotFound();

        nv.HoTen = hoTen;
        nv.Sdt = sdt;
        nv.PhongBan = phongBan;
        nv.VaiTro = vaiTro;
        nv.TrangThai = trangThai;
        await _db.SaveChangesAsync();

        TempData["ThongBao"] = $"Đã cập nhật tài khoản \"{hoTen}\".";
        return RedirectToAction(nameof(DanhSach));
    }
}
